﻿//
// Copyright 2024 Google LLC
//
// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements.  See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership.  The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at
// 
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.
//

using Google.Solutions.Common.Interop;
using Google.Solutions.Platform.Interop;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Google.Solutions.Platform.IO
{
    public class PseudoConsoleException : IOException
    {
        public PseudoConsoleException(string message) : base(message)
        {
        }

        public PseudoConsoleException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        internal static PseudoConsoleException FromHresult(
            HRESULT hresult,
            string message)
        {
            return new PseudoConsoleException(
                $"{message} (HRESULT 0x{hresult:X})",
                new ExternalException(message, (int)hresult));
        }
    }
}
