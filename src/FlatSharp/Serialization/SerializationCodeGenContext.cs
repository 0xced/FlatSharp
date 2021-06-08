﻿/*
 * Copyright 2020 James Courtney
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace FlatSharp
{
    using FlatSharp.TypeModel;
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Code gen context for serialization methods.
    /// </summary>
    public record SerializationCodeGenContext
    {
        public SerializationCodeGenContext(
            string serializationContextVariableName,
            string spanVariableName,
            string spanWriterVariableName,
            string valueVariableName,
            string offsetVariableName,
            bool isOffsetByRef,
            IReadOnlyDictionary<Type, string> methodNameMap,
            TypeModelContainer typeModelContainer,
            FlatBufferSerializerOptions options)
        {
            this.SerializationContextVariableName = serializationContextVariableName;
            this.SpanWriterVariableName = spanWriterVariableName;
            this.SpanVariableName = spanVariableName;
            this.ValueVariableName = valueVariableName;
            this.OffsetVariableName = offsetVariableName;
            this.MethodNameMap = methodNameMap;
            this.TypeModelContainer = typeModelContainer;
            this.IsOffsetByRef = isOffsetByRef;
            this.Options = options;
        }

        /// <summary>
        /// The variable name of the serialization context. Represents a <see cref="SerializationContext"/> value.
        /// </summary>
        public string SerializationContextVariableName { get; init; }

        /// <summary>
        /// The variable name of the span. Represents a <see cref="System.Span{System.Byte}"/> value.
        /// </summary>
        public string SpanVariableName { get; init; }

        /// <summary>
        /// The variable name of the span writer. Represents a <see cref="SpanWriter"/> value.
        /// </summary>
        public string SpanWriterVariableName { get; init; }

        /// <summary>
        /// The variable name of the current value to serialize.
        /// </summary>
        public string ValueVariableName { get; init; }

        /// <summary>
        /// The variable name of the offset in the span. Represents a <see cref="Int32"/> value.
        /// </summary>
        public string OffsetVariableName { get; init; }

        /// <summary>
        /// Indicates if the offset is passed by reference.
        /// </summary>
        public bool IsOffsetByRef { get; init; }

        /// <summary>
        /// A mapping of type to serialize method name for that type.
        /// </summary>
        public IReadOnlyDictionary<Type, string> MethodNameMap { get; private init; }

        /// <summary>
        /// Resolves Type -> TypeModel.
        /// </summary>
        public TypeModelContainer TypeModelContainer { get; private init; }

        /// <summary>
        /// Serialization options.
        /// </summary>
        public FlatBufferSerializerOptions Options { get; private init; }

        /// <summary>
        /// Gets a serialization invocation for the given type.
        /// </summary>
        public string GetSerializeInvocation(Type type)
        {
            ITypeModel typeModel = this.TypeModelContainer.CreateTypeModel(type);
            string byRef = string.Empty;
            if (this.IsOffsetByRef)
            {
                byRef = "ref ";
            }

            if (typeModel.SerializeMethodRequiresContext)
            {
                return $"{this.MethodNameMap[type]}({this.SpanWriterVariableName}, {this.SpanVariableName}, {this.ValueVariableName}, {byRef}{this.OffsetVariableName}, {this.SerializationContextVariableName})";
            }
            else
            {
                return $"{this.MethodNameMap[type]}({this.SpanWriterVariableName}, {this.SpanVariableName}, {this.ValueVariableName}, {byRef}{this.OffsetVariableName})";
            }
        }
    }
}
