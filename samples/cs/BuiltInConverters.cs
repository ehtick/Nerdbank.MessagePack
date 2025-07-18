﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace OptionalConverters;

internal class BuiltInConverters
{
    #region STJOptionalConverters
    private static readonly MessagePackSerializer Serializer = new MessagePackSerializer()
        .WithSystemTextJsonConverters();
    #endregion
}
