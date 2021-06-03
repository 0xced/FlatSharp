﻿/*
 * Copyright 2021 James Courtney
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
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using FlatSharp.TypeModel;

    internal class DeserializeClassDefinition
    {
        internal const string PoolTracingPragma = "FLATSHARP_POOL_DIAGNOSTICS";

        private const string InputBufferVariableName = "__buffer";
        private const string OffsetVariableName = "__offset";
        private const string VTableLocationVariableName = "__vtableOffset";
        private const string VTableMaxIndexVariableName = "__vtableMaxIndex";

        private readonly ITypeModel typeModel;
        private readonly FlatBufferSerializerOptions options;

        private readonly List<string> fieldDefinitions = new();
        private readonly List<string> propertyOverrides = new();
        private readonly List<string> initializeStatements = new();
        private readonly HashSet<string> releaseStatements = new();
        private readonly List<string> readMethods = new();
        private readonly HashSet<string> maskDefinitions = new();

        private readonly string vtableOffsetAccessor;
        private readonly string vtableMaxIndexAccessor;

        private readonly MethodInfo? onDeserializeMethod;

        public DeserializeClassDefinition(
            string className,
            MethodInfo? onDeserializeMethod,
            ITypeModel typeModel,
            FlatBufferSerializerOptions options)
        {
            this.ClassName = className;
            this.typeModel = typeModel;
            this.options = options;
            this.onDeserializeMethod = onDeserializeMethod;

            if (!this.options.GreedyDeserialize)
            {
                // maintain reference to buffer.
                this.fieldDefinitions.Add($"private TInputBuffer {InputBufferVariableName};");
                this.fieldDefinitions.Add($"private int {OffsetVariableName};");

                this.initializeStatements.Add($"this.{InputBufferVariableName} = buffer;");
                this.initializeStatements.Add($"this.{OffsetVariableName} = offset;");

                this.releaseStatements.Add($"this.{InputBufferVariableName} = default;");
                this.releaseStatements.Add($"this.{OffsetVariableName} = default;");
            }

            if (this.typeModel.SchemaType == FlatBufferSchemaType.Table)
            {
                this.vtableMaxIndexAccessor = "__vtableMaxIndex";
                this.vtableOffsetAccessor = "__vtableLocation";

                this.initializeStatements.Add(
                    $"buffer.{nameof(InputBufferExtensions.InitializeVTable)}(offset, out var {this.vtableOffsetAccessor}, out var {this.vtableMaxIndexAccessor});");

                if (!options.GreedyDeserialize)
                {
                    this.fieldDefinitions.Add($"private int {VTableLocationVariableName};");
                    this.fieldDefinitions.Add($"private int {VTableMaxIndexVariableName};");

                    this.initializeStatements.Add($"this.{VTableLocationVariableName} = {this.vtableOffsetAccessor};");
                    this.initializeStatements.Add($"this.{VTableMaxIndexVariableName} = {this.vtableMaxIndexAccessor};");

                    this.releaseStatements.Add($"this.{VTableLocationVariableName} = default;");
                    this.releaseStatements.Add($"this.{VTableMaxIndexVariableName} = default;");

                    this.vtableMaxIndexAccessor = $"this.{VTableMaxIndexVariableName}";
                    this.vtableOffsetAccessor = $"this.{VTableLocationVariableName}";
                }
            }
            else
            {
                this.vtableMaxIndexAccessor = "default";
                this.vtableOffsetAccessor = "default";
            }
        }

        public bool HasEmbeddedBufferReference => !this.options.GreedyDeserialize;

        public string ClassName { get; }

        public void AddProperty(ItemMemberModel itemModel, string readValueMethodName, string writeValueMethodName)
        {
            this.AddFieldDefinitions(itemModel);
            this.AddPropertyDefinitions(itemModel, writeValueMethodName);
            this.AddCtorStatements(itemModel);
            this.AddReadMethod(itemModel, readValueMethodName);

            if (itemModel.IsWriteThrough)
            {
                if (this.options.DeserializationOption != FlatBufferDeserializationOption.VectorCacheMutable)
                {
                    throw new InvalidFlatBufferDefinitionException(
                        $"Property '{itemModel.PropertyInfo.Name}' of {this.typeModel.SchemaType} '{this.typeModel.GetCompilableTypeName()}' specifies the WriteThrough option. However, WriteThrough is only supported when using deserialization option 'VectorCacheMutable'.");
                }

                this.AddWriteThroughMethod(itemModel, writeValueMethodName);
            }
        }

        private void AddFieldDefinitions(ItemMemberModel itemModel)
        {
            if (this.options.Lazy || !itemModel.IsVirtual)
            {
                return;
            }

            if (!this.options.GreedyDeserialize)
            {
                this.maskDefinitions.Add($"private byte {GetHasValueFieldName(itemModel)};");
                this.releaseStatements.Add($"this.{GetHasValueFieldName(itemModel)} = default;");
            }

            string typeName = itemModel.GetNullableAnnotationTypeName(this.typeModel.SchemaType);

            this.fieldDefinitions.Add($"private {typeName} {GetFieldName(itemModel)};");
            this.releaseStatements.Add($"this.{GetFieldName(itemModel)} = default;");
        }

        private void AddReadMethod(ItemMemberModel itemModel, string readValueMethodName)
        {
            string body = itemModel.CreateReadItemBody(
                readValueMethodName,
                "buffer",
                "offset",
                "vtableOffset",
                "maxVtableIndex");

            string typeName = itemModel.GetNullableAnnotationTypeName(this.typeModel.SchemaType);
            this.readMethods.Add(
                $@"
                {GetAggressiveInliningAttribute()}
                private static {typeName} {GetReadIndexMethodName(itemModel)}(
                    TInputBuffer buffer, 
                    int offset, 
                    int vtableOffset, 
                    int maxVtableIndex)
                {{
                    {body};
                }}");
        }

        private void AddWriteThroughMethod(ItemMemberModel itemModel, string writeValueMethodName)
        {
            this.readMethods.Add(
                $@"
                    {GetAggressiveInliningAttribute()}
                    private static void {GetWriteMethodName(itemModel)}(
                        TInputBuffer buffer,
                        int offset,
                        {itemModel.ItemTypeModel.GetCompilableTypeName()} value)
                    {{
                        {itemModel.CreateWriteThroughBody(writeValueMethodName, "buffer", "offset", "value")}
                    }}");
        }

        private void AddPropertyDefinitions(ItemMemberModel itemModel, string writeValueMethodName)
        {
            if (!itemModel.IsVirtual)
            {
                return;
            }

            string getterBody;
            string setter = string.Empty;
            var accessModifiers = CSharpHelpers.GetPropertyAccessModifiers(itemModel.PropertyInfo);

            {
                string readUnderlyingInvocation = $"{GetReadIndexMethodName(itemModel)}(this.{InputBufferVariableName}, this.{OffsetVariableName}, {this.vtableOffsetAccessor}, {this.vtableMaxIndexAccessor})";
                if (this.options.GreedyDeserialize)
                {
                    getterBody = $"return this.{GetFieldName(itemModel)};";
                }
                else if (this.options.Lazy)
                {
                    getterBody = $"return {readUnderlyingInvocation};";
                }
                else
                {
                    getterBody = $@"
                            if ((this.{GetHasValueFieldName(itemModel)} & {GetHasValueFieldMask(itemModel)}) == 0)
                            {{
                                this.{GetFieldName(itemModel)} = {readUnderlyingInvocation};
                                this.{GetHasValueFieldName(itemModel)} |= {GetHasValueFieldMask(itemModel)};
                            }}
                            return this.{GetFieldName(itemModel)};
                        ";
                }
            }

            MethodInfo? setMethod = itemModel.PropertyInfo.SetMethod;
            if (setMethod is not null)
            {
                string verb = "set";
                string setterBody;

                // see if set is init-only.
                bool isInitOnly = setMethod.ReturnParameter.GetRequiredCustomModifiers().Any(
                    x => x.FullName == "System.Runtime.CompilerServices.IsExternalInit");

                if (isInitOnly)
                {
                    verb = "init";
                }

                if (!this.options.GenerateMutableObjects)
                {
                    setterBody = $"throw new NotMutableException();";
                }
                else if (this.options.GreedyDeserialize)
                {
                    setterBody = $"this.{GetFieldName(itemModel)} = value;";
                }
                else
                {
                    setterBody = $"this.{GetFieldName(itemModel)} = value; this.{GetHasValueFieldName(itemModel)} |= {GetHasValueFieldMask(itemModel)};";
                    if (itemModel.IsWriteThrough)
                    {
                        setterBody += $"{GetWriteMethodName(itemModel)}({this.GetBufferReference()}, {OffsetVariableName}, value);";
                    }
                }

                setter = $@"
                    {accessModifiers.setModifier.ToCSharpString()} {verb} 
                    {{ 
#if {PoolTracingPragma}
                        if (this.released)
                        {{
                            throw new InvalidOperationException(
                                {this.CreatePoolErrorMessage("FlatSharp pooled object used after release.")});
                        }}
#endif
                        {setterBody} 
                    }}";
            }

            string typeName = itemModel.GetNullableAnnotationTypeName(this.typeModel.SchemaType);
            this.propertyOverrides.Add($@"
                {accessModifiers.propertyModifier.ToCSharpString()} override {typeName} {itemModel.PropertyInfo.Name}
                {{ 
                    {accessModifiers.getModifer.ToCSharpString()} get
                    {{
#if {PoolTracingPragma}
                        if (this.released)
                        {{
                            throw new InvalidOperationException(
                                {this.CreatePoolErrorMessage("FlatSharp pooled object used after release.")});
                        }}
#endif
                        {getterBody}
                    }}
                    {setter}
                }}");
        }

        private void AddCtorStatements(ItemMemberModel itemModel)
        {
            var classification = itemModel.ItemTypeModel.ClassifyContextually(this.typeModel.SchemaType);

            string assignment = $"base.{itemModel.PropertyInfo.Name}";
            if (itemModel.IsVirtual)
            {
                assignment = $"this.{GetFieldName(itemModel)}";
            }

            if (this.options.GreedyDeserialize || !itemModel.IsVirtual)
            {
                this.initializeStatements.Add($"{assignment} = {GetReadIndexMethodName(itemModel)}(buffer, offset, {this.vtableOffsetAccessor}, {this.vtableMaxIndexAccessor});");
            }
            else if (!this.options.Lazy)
            {
                if (classification.IsRequiredReference() || (classification.IsOptionalReference() && itemModel.IsRequired))
                {
                    this.initializeStatements.Add($"{assignment} = null!;");
                }
            }
        }

        public override string ToString()
        {
            ConstructorInfo? ctor = this.typeModel.PreferredSubclassConstructor;
            if (ctor is null)
            {
                throw new InvalidFlatBufferDefinitionException($"Unable to find a usable subclass constructor for '{this.typeModel.GetCompilableTypeName()}'.");
            }

            string onDeserializedStatement = string.Empty;
            if (this.onDeserializeMethod is not null)
            {
                onDeserializedStatement = $"base.{this.onDeserializeMethod.Name}(__CtorContext);";
            }

            string baseParams = string.Empty;
            if (ctor.GetParameters().Length != 0)
            {
                baseParams = "__CtorContext";
            }

            return 
            $@"
                private sealed class {this.ClassName}<TInputBuffer> 
                    : {typeModel.GetCompilableTypeName()} 
                    , {typeof(IFlatBufferDeserializedObject).GetCompilableTypeName()}
                    where TInputBuffer : IInputBuffer
                {{
                    private static readonly System.Collections.Concurrent.ConcurrentBag<{this.ClassName}<TInputBuffer>> __Pool
                        = new System.Collections.Concurrent.ConcurrentBag<{this.ClassName}<TInputBuffer>>();

                    private static readonly {typeof(FlatBufferDeserializationContext).GetCompilableTypeName()} __CtorContext 
                        = new {typeof(FlatBufferDeserializationContext).GetCompilableTypeName()}({typeof(FlatBufferDeserializationOption).GetCompilableTypeName()}.{options.DeserializationOption});

                    {string.Join("\r\n", this.fieldDefinitions)}
                    {string.Join("\r\n", this.maskDefinitions)}

#if {PoolTracingPragma}
                    private string allocationStack;
                    private string releaseStack;
                    private volatile bool released;
#endif

                    public static {this.ClassName}<TInputBuffer> GetOrCreate(TInputBuffer buffer, int offset)
                    {{
                        if (!__Pool.TryTake(out var item))
                        {{
                            item = new {this.ClassName}<TInputBuffer>();
                        }}

                        item.Initialize(buffer, offset);

#if {PoolTracingPragma}
                        item.allocationStack = Environment.StackTrace;
#endif

                        return item;
                    }}

                    private {this.ClassName}() : base({baseParams}) {{ }}

                    private void Initialize(TInputBuffer buffer, int offset)
                    {{
                        {string.Join("\r\n", this.initializeStatements)}
                        {onDeserializedStatement}
                    }}

                    {typeof(Type).GetCompilableTypeName()} {nameof(IFlatBufferDeserializedObject)}.{nameof(IFlatBufferDeserializedObject.TableOrStructType)} => typeof({typeModel.GetCompilableTypeName()});
                    {typeof(FlatBufferDeserializationContext).GetCompilableTypeName()} {nameof(IFlatBufferDeserializedObject)}.{nameof(IFlatBufferDeserializedObject.DeserializationContext)} => __CtorContext;
                    {typeof(IInputBuffer).GetCompilableTypeName()}? {nameof(IFlatBufferDeserializedObject)}.{nameof(IFlatBufferDeserializedObject.InputBuffer)} => {this.GetBufferReference()};

                    void {nameof(IFlatBufferDeserializedObject)}.{nameof(IFlatBufferDeserializedObject.Release)}()
                    {{
                        {string.Join("\r\n", this.releaseStatements)}

#if {PoolTracingPragma}
                        if (this.released)
                        {{
                            throw new InvalidOperationException(
                                {this.CreatePoolErrorMessage("FlatSharp pooled object released twice.")});
                        }}

                        this.released = true;
                        this.releaseStack = Environment.StackTrace;
#else
                        __Pool.Add(this);
#endif
                    }}

                    {string.Join("\r\n", this.propertyOverrides)}

                    {string.Join("\r\n", this.readMethods)}
                }}
";
        }

        private string CreatePoolErrorMessage(string message)
        {
            string type = this.typeModel.GetCompilableTypeName();
            return $@"$""{message} Type = '{type}', \r\n\r\n AllocationStack = '{{this.allocationStack}}', \r\n\r\n ReleaseStack = '{{this.releaseStack}}'""";
        }

        private static string GetFieldName(ItemMemberModel itemModel) => $"__index{itemModel.Index}Value";

        private static string GetHasValueFieldName(int index) => $"__mask{index}";

        private static string GetHasValueFieldName(ItemMemberModel itemModel) => GetHasValueFieldName(itemModel.Index / 8);

        private static string GetHasValueFieldMask(ItemMemberModel itemModel) => $"(byte){1 << (itemModel.Index % 8)}";

        private static string GetWriteMethodName(ItemMemberModel itemModel) => $"WriteIndex{itemModel.Index}Value";

        private static string GetReadIndexMethodName(ItemMemberModel itemModel) => $"ReadIndex{itemModel.Index}Value";

        private static string GetAggressiveInliningAttribute()
        {
            string inlining = "System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining";
            string attribute = $"[{typeof(System.Runtime.CompilerServices.MethodImplAttribute).FullName}({inlining})]";
            return attribute;
        }

        private string GetBufferReference()
        {
            if (this.HasEmbeddedBufferReference)
            {
                return $"this.{InputBufferVariableName}";
            }

            return "null";
        }
    }
}
