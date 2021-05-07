// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.AspNetCore.HttpLogging
{
    internal sealed class HttpRequestLog : IReadOnlyList<KeyValuePair<string, string?>>
    {
        private readonly List<KeyValuePair<string, string?>> _keyValues;
        private string? _cachedToString;

        internal static readonly Func<object, Exception?, string> Callback = (state, exception) => ((HttpRequestLog)state).ToString();

        public HttpRequestLog(List<KeyValuePair<string, string?>> keyValues)
        {
            _keyValues = keyValues;
        }

        public KeyValuePair<string, string?> this[int index] => _keyValues[index];

        public int Count => _keyValues.Count;

        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator()
        {
            var count = _keyValues.Count;
            for (var i = 0; i < count; i++)
            {
                yield return _keyValues[i];
            }
        }

        public override string ToString()
        {
            if (_cachedToString == null)
            {
                // TODO use string.Create instead of a StringBuilder here.
                var builder = new StringBuilder();
                var count = _keyValues.Count;
                builder.Append("Request:");
                builder.Append(Environment.NewLine);

                for (var i = 0; i < count - 1; i++)
                {
                    var kvp = _keyValues[i];
                    builder.Append(kvp.Key);
                    builder.Append(": ");
                    builder.Append(kvp.Value);
                    builder.Append(Environment.NewLine);
                }

                if (count > 0)
                {
                    var kvp = _keyValues[count - 1];
                    builder.Append(kvp.Key);
                    builder.Append(": ");
                    builder.Append(kvp.Value);
                }

                _cachedToString = builder.ToString();
            }

            return _cachedToString;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
