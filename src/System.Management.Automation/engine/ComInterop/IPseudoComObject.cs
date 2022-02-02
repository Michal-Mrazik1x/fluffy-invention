// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Dynamic;
using System.Linq.Expressions;

namespace System.Management.Automation.ComInterop
{
    internal interface IPseudoComObject
    {
        DynamicMetaObject GetMetaObject(Expression expression);
    }
}
