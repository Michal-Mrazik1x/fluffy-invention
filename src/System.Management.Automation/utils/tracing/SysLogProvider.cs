// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if UNIX

using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using System.Management.Automation.Internal;

namespace System.Management.Automation.Tracing
{
    /// <summary>
    /// Encapsulates the message resource and SysLog logging for an ETW event.
    /// The other half of the partial class is generated by EtwGen and contains a
    /// static dictionary containing the event id mapped to the associated event meta data
    /// and resource string reference.
    /// </summary>
    /// <remarks>
    /// This component logs ETW trace events to syslog.
    /// The log entries use the following common format
    ///     (commitId:threadId:channelid) [context] payload
    /// Where:
    ///     commitId: A hash code of the full git commit id string.
    ///     threadid: The thread identifier of calling code.
    ///     channelid: The identifier for the output channel. See PSChannel for values.
    ///     context: Dependent on the type of log entry.
    ///     payload: Dependent on the type of log entry.
    /// Note:
    ///     commitId, threadId, and eventId are logged as HEX without a leading
    ///     '0x'.
    ///
    /// 4 types of log entries are produced.
    /// NOTE: Where constant string are logged, the template places the string in
    /// double quotes. For example, the GitCommitId log entry uses "GitCommitId"
    /// for the context value.
    ///
    /// Note that the examples illustrate the output from SysLogProvider.Log,
    /// Data automatically prepended by syslog, such as timestamp, hostname, ident,
    /// and processid are not shown.
    ///
    /// GitCommitId
    ///   This is the first log entry for a session. It provides a correlation
    ///   between the full git commit id string and a hash code used for subsequent
    ///   log entries.
    ///   Context: "GitCommitId"
    ///   Payload: string "Hash:" hashcode as HEX string.
    ///    For official builds, the GitCommitID is the release tag. For other builds the commit id may include an SHA-1 hash at the
    ///    end of the release tag.
    ///   Example 1: Official release
    ///     (19E1025:3:10) [GitCommitId] v6.0.0-beta.9 Hash:64D0C08D
    ///   Example 2: Commit id with SHA-1 hash
    ///     (19E1025:3:10) [GitCommitId] v6.0.0-beta.8-67-gca2630a3dea6420a3cd3914c84a74c1c45311f54 Hash:8EE3A3B3
    ///
    /// Transfer
    ///   A log entry to record a transfer event.
    ///   Context: "Transfer"
    ///   The playload is two, space separated string guids, the first being the
    ///   parent activityid followed by the new activityid.
    ///   Example: (19E1025:3:10) [Transfer] {de168a71-6bb9-47e4-8712-bc02506d98be} {ab0077f6-c042-4728-be76-f688cfb1b054}
    ///
    /// Activity
    ///   A log entry for when activity is set.
    ///   Context: "Activity"
    ///   Payload: The string guid of the activity id.
    ///   Example: (19E1025:3:10) [Activity] {ab0077f6-c042-4728-be76-f688cfb1b054}
    ///
    ///  Event
    ///   Application logging (Events)
    ///   Context: EventId:taskname.opcodename.levelname
    ///   Payload: The event's message text formatted with arguments from the caller.
    ///   Example: (19E1025:3:10) [Perftrack_ConsoleStartupStart:PowershellConsoleStartup.WinStart.Informational] PowerShell console is starting up
    /// </remarks>
    internal class SysLogProvider
    {
        // Ensure the string pointer is not garbage collected.
        private static IntPtr _nativeSyslogIdent = IntPtr.Zero;
        private static readonly NativeMethods.SysLogPriority _facility = NativeMethods.SysLogPriority.Local0;

        private readonly byte _channelFilter;
        private readonly ulong _keywordFilter;
        private readonly byte _levelFilter;

        /// <summary>
        /// Initializes a new instance of this class.
        /// </summary>
        /// <param name="applicationId">The log identity name used to identify the application in syslog.</param>
        /// <param name="level">The trace level to enable.</param>
        /// <param name="keywords">The keywords to enable.</param>
        /// <param name="channels">The output channels to enable.</param>
        public SysLogProvider(string applicationId, PSLevel level, PSKeyword keywords, PSChannel channels)
        {
            // NOTE: This string needs to remain valid for the life of the process since the underlying API keeps
            // a reference to it.
            // FUTURE: If logging is redesigned, make these details static or a singleton since there should only be one
            // instance active.
            _nativeSyslogIdent = Marshal.StringToHGlobalAnsi(applicationId);
            NativeMethods.OpenLog(_nativeSyslogIdent, _facility);
            _keywordFilter = (ulong)keywords;
            _levelFilter = (byte)level;
            _channelFilter = (byte)channels;
            if ((_channelFilter & (ulong)PSChannel.Operational) != 0)
            {
                _keywordFilter |= (ulong)PSKeyword.UseAlwaysOperational;
            }

            if ((_channelFilter & (ulong)PSChannel.Analytic) != 0)
            {
                _keywordFilter |= (ulong)PSKeyword.UseAlwaysAnalytic;
            }
        }

        /// <summary>
        /// Defines a thread local StringBuilder for building log messages.
        /// </summary>
        /// <remarks>
        /// NOTE: do not access this field directly, use the MessageBuilder
        /// property to ensure correct thread initialization; otherwise, a null reference can occur.
        /// </remarks>
        [ThreadStatic]
        private static StringBuilder t_messageBuilder;

        private static StringBuilder MessageBuilder
        {
            get
            {
                if (t_messageBuilder == null)
                {
                    // NOTE: Thread static fields must be explicitly initialized for each thread.
                    t_messageBuilder = new StringBuilder(200);
                }

                return t_messageBuilder;
            }
        }

        /// <summary>
        /// Defines a activity id for the current thread.
        /// </summary>
        /// <remarks>
        /// NOTE: do not access this field directly, use the Activity property
        /// to ensure correct thread initialization.
        /// </remarks>
        [ThreadStatic]
        private static Guid? t_activity;

        private static Guid Activity
        {
            get
            {
                if (!t_activity.HasValue)
                {
                    // NOTE: Thread static fields must be explicitly initialized for each thread.
                    t_activity = Guid.NewGuid();
                }

                return t_activity.Value;
            }

            set
            {
                t_activity = value;
            }
        }

        /// <summary>
        /// Gets the value indicating if the specified level and keywords are enabled for logging.
        /// </summary>
        /// <param name="level">The PSLevel to check.</param>
        /// <param name="keywords">The PSKeyword to check.</param>
        /// <returns>True if the specified level and keywords are enabled for logging.</returns>
        internal bool IsEnabled(PSLevel level, PSKeyword keywords)
        {
            return ((ulong)keywords & _keywordFilter) != 0
                && ((int)level <= _levelFilter);
        }

        // NOTE: There are a number of places where PowerShell code sends analytic events
        // to the operational channel. This is a side-effect of the custom wrappers that
        // use flags that are not consistent with the event definition.
        // To ensure filtering of analytic events is consistent, both keyword and channel
        // filtering is performed to suppress analytic events.
        private bool ShouldLog(PSLevel level, PSKeyword keywords, PSChannel channel)
        {
            return (_channelFilter & (ulong)channel) != 0
                && IsEnabled(level, keywords);
        }

#region resource manager

        private static global::System.Resources.ResourceManager _resourceManager;
        private static global::System.Globalization.CultureInfo _resourceCulture;

        private static global::System.Resources.ResourceManager ResourceManager
        {
            get
            {
                if (_resourceManager is null)
                {
                    _resourceManager = new global::System.Resources.ResourceManager("System.Management.Automation.resources.EventResource", typeof(EventResource).Assembly);
                }

                return _resourceManager;
            }
        }

        /// <summary>
        ///   Overrides the current threads CurrentUICulture property for all
        ///   resource lookups using this strongly typed resource class.
        /// </summary>
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        internal static global::System.Globalization.CultureInfo Culture
        {
            get
            {
                return _resourceCulture;
            }

            set
            {
                _resourceCulture = value;
            }
        }

        private static string GetResourceString(string resourceName)
        {
            string value = ResourceManager.GetString(resourceName, Culture);
            if (string.IsNullOrEmpty(value))
            {
                value = string.Format(CultureInfo.InvariantCulture, "Unknown resource: {0}", resourceName);
                Diagnostics.Assert(false, value);
            }

            return value;
        }

#endregion resource manager

        /// <summary>
        /// Gets the EventMessage for a given event.
        /// </summary>
        /// <param name="sb">The StringBuilder to append.</param>
        /// <param name="eventId">The id of the event to retrieve.</param>
        /// <param name="args">An array of zero or more payload objects.</param>
        private static void GetEventMessage(StringBuilder sb, PSEventId eventId, params object[] args )
        {
            int parameterCount;
            string resourceName = EventResource.GetMessage((int)eventId, out parameterCount);

            if (resourceName == null)
            {
                // If an event id was specified that is not found in the event resource lookup table,
                // use a placeholder message that includes the event id.
                resourceName = EventResource.GetMissingEventMessage(out parameterCount);
                Diagnostics.Assert(false, sb.ToString());
                args = new object[] {eventId};
            }

            string resourceValue = GetResourceString(resourceName);
            if (parameterCount > 0)
            {
                sb.AppendFormat(resourceValue, args);
            }
            else
            {
                sb.Append(resourceValue);
            }
        }

#region logging

        // maps a LogLevel to an associated SysLogPriority.
        private static readonly NativeMethods.SysLogPriority[] _levels =
        {
            NativeMethods.SysLogPriority.Info,
            NativeMethods.SysLogPriority.Critical,
            NativeMethods.SysLogPriority.Error,
            NativeMethods.SysLogPriority.Warning,
            NativeMethods.SysLogPriority.Info,
            NativeMethods.SysLogPriority.Info
        };

        /// <summary>
        /// Logs a activity transfer.
        /// </summary>
        /// <param name="parentActivityId">The parent activity id.</param>
        public void LogTransfer(Guid parentActivityId)
        {
            // NOTE: always log
            int threadId = Environment.CurrentManagedThreadId;
            string message = string.Format(CultureInfo.InvariantCulture,
                                           "({0}:{1:X}:{2:X}) [Transfer]:{3} {4}",
                                           PSVersionInfo.GitCommitId, threadId, PSChannel.Operational,
                                           parentActivityId.ToString("B"),
                                           Activity.ToString("B"));

            NativeMethods.SysLog(NativeMethods.SysLogPriority.Info, message);
        }

        /// <summary>
        /// Logs the activity identifier for the current thread.
        /// </summary>
        /// <param name="activity">The Guid activity identifier.</param>
        public void SetActivity(Guid activity)
        {
            int threadId = Environment.CurrentManagedThreadId;
            Activity = activity;

            // NOTE: always log
            string message = string.Format(CultureInfo.InvariantCulture,
                                           "({0:X}:{1:X}:{2:X}) [Activity] {3}",
                                           PSVersionInfo.GitCommitId, threadId, PSChannel.Operational, activity.ToString("B"));
            NativeMethods.SysLog(NativeMethods.SysLogPriority.Info, message);
        }

        /// <summary>
        /// Writes a log entry.
        /// </summary>
        /// <param name="eventId">The event id of the log entry.</param>
        /// <param name="channel">The channel to log.</param>
        /// <param name="task">The task for the log entry.</param>
        /// <param name="opcode">The operation for the log entry.</param>
        /// <param name="level">The logging level.</param>
        /// <param name="keyword">The keyword(s) for the event.</param>
        /// <param name="args">The payload for the log message.</param>
        public void Log(PSEventId eventId, PSChannel channel, PSTask task, PSOpcode opcode, PSLevel level, PSKeyword keyword, params object[] args)
        {
            if (ShouldLog(level, keyword, channel))
            {
                int threadId = Environment.CurrentManagedThreadId;

                StringBuilder sb = MessageBuilder;
                sb.Clear();

                // add the message preamble
                sb.AppendFormat(CultureInfo.InvariantCulture,
                                "({0}:{1:X}:{2:X}) [{3:G}:{4:G}.{5:G}.{6:G}] ",
                                PSVersionInfo.GitCommitId, threadId, channel, eventId, task, opcode, level);

                // add the message
                GetEventMessage(sb, eventId, args);

                NativeMethods.SysLogPriority priority;
                if ((int)level <= _levels.Length)
                {
                    priority = _levels[(int)level];
                }
                else
                {
                    priority = NativeMethods.SysLogPriority.Info;
                }
                // log it.
                NativeMethods.SysLog(priority, sb.ToString());
            }
        }

#endregion logging
    }

    internal enum LogLevel : uint
    {
        Always = 0,
        Critical = 1,
        Error = 2,
        Warning = 3,
        Information = 4,
        Verbose = 5
    }

    internal static class NativeMethods
    {
        private const string libpslnative = "libpsl-native";
        /// <summary>
        /// Write a message to the system logger, which in turn writes the message to the system console, log files, etc.
        /// See man 3 syslog for more info.
        /// </summary>
        /// <param name="priority">
        /// The OR of a priority and facility in the SysLogPriority enum indicating the the priority and facility of the log entry.
        /// </param>
        /// <param name="message">The message to put in the log entry.</param>
        [DllImport(libpslnative, CharSet = CharSet.Ansi, EntryPoint = "Native_SysLog")]
        internal static extern void SysLog(SysLogPriority priority, string message);

        [DllImport(libpslnative, CharSet = CharSet.Ansi, EntryPoint = "Native_OpenLog")]
        internal static extern void OpenLog(IntPtr ident, SysLogPriority facility);

        [DllImport(libpslnative, EntryPoint = "Native_CloseLog")]
        internal static extern void CloseLog();

        [Flags]
        internal enum SysLogPriority : uint
        {
            // Priorities enum values.

            /// <summary>
            /// System is unusable.
            /// </summary>
            Emergency       = 0,

            /// <summary>
            /// Action must be taken immediately.
            /// </summary>
            Alert           = 1,

            /// <summary>
            /// Critical conditions.
            /// </summary>
            Critical        = 2,

            /// <summary>
            /// Error conditions.
            /// </summary>
            Error           = 3,

            /// <summary>
            /// Warning conditions.
            /// </summary>
            Warning         = 4,

            /// <summary>
            /// Normal but significant condition.
            /// </summary>
            Notice          = 5,

            /// <summary>
            /// Informational.
            /// </summary>
            Info            = 6,

            /// <summary>
            /// Debug-level messages.
            /// </summary>
            Debug           = 7,

            // Facility enum values.

            /// <summary>
            /// Kernel messages.
            /// </summary>
            Kernel          = (0 << 3),

            /// <summary>
            /// Random user-level messages.
            /// </summary>
            User            = (1 << 3),

            /// <summary>
            /// Mail system.
            /// </summary>
            Mail            = (2 << 3),

            /// <summary>
            /// System daemons.
            /// </summary>
            Daemon          = (3 << 3),

            /// <summary>
            /// Authorization messages.
            /// </summary>
            Authorization   = (4 << 3),

            /// <summary>
            /// Messages generated internally by syslogd.
            /// </summary>
            Syslog          = (5 << 3),

            /// <summary>
            /// Line printer subsystem.
            /// </summary>
            Lpr             = (6 << 3),

            /// <summary>
            /// Network news subsystem.
            /// </summary>
            News            = (7 << 3),

            /// <summary>
            /// UUCP subsystem.
            /// </summary>
            Uucp            = (8 << 3),

            /// <summary>
            /// Clock daemon.
            /// </summary>
            Cron            = (9 << 3),

            /// <summary>
            /// Security/authorization messages (private)
            /// </summary>
            Authpriv        = (10 << 3),

            /// <summary>
            /// FTP daemon.
            /// </summary>
            Ftp             = (11 << 3),

            // Reserved for system use

            /// <summary>
            /// Reserved for local use.
            /// </summary>
            Local0          = (16 << 3),
            /// <summary>
            /// Reserved for local use.
            /// </summary>
            Local1          = (17 << 3),
            /// <summary>
            /// Reserved for local use.
            /// </summary>
            Local2          = (18 << 3),
            /// <summary>
            /// Reserved for local use.
            /// </summary>
            Local3          = (19 << 3),
            /// <summary>
            /// Reserved for local use.
            /// </summary>
            Local4          = (20 << 3),
            /// <summary>
            /// Reserved for local use.
            /// </summary>
            Local5          = (21 << 3),
            /// <summary>
            /// Reserved for local use.
            /// </summary>
            Local6          = (22 << 3),
            /// <summary>
            /// Reserved for local use.
            /// </summary>
            Local7          = (23 << 3),
        }
    }
}

#endif // UNIX
