﻿/*
 * Copyright 2018 James Courtney
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
 
 namespace FlatSharp.TypeModel
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using FlatSharp.Attributes;

    /// <summary>
    /// Defines the type schema for a Flatbuffer struct. Structs, like C structs, are statically ordered sets of data
    /// whose schema may not be changed.
    /// </summary>
    public class StructTypeModel : RuntimeTypeModel
    {
        private readonly List<StructMemberModel> memberTypes = new List<StructMemberModel>();
        private int inlineSize;
        private int maxAlignment = 1;

        internal StructTypeModel(Type clrType, TypeModelContainer container) : base(clrType, container)
        {
        }

        /// <summary>
        /// Gets the schema type.
        /// </summary>
        public override FlatBufferSchemaType SchemaType => FlatBufferSchemaType.Struct;

        /// <summary>
        /// Layout of the vtable.
        /// </summary>
        public override ImmutableArray<PhysicalLayoutElement> PhysicalLayout =>
            new PhysicalLayoutElement[] { new PhysicalLayoutElement(this.inlineSize, this.maxAlignment) }.ToImmutableArray();

        /// <summary>
        /// Structs are composed of scalars.
        /// </summary>
        public override bool IsFixedSize => true;

        /// <summary>
        /// Structs can be part of structs.
        /// </summary>
        public override bool IsValidStructMember => true;

        /// <summary>
        /// Structs can be part of tables.
        /// </summary>
        public override bool IsValidTableMember => true;

        /// <summary>
        /// Structs can be part of unions.
        /// </summary>
        public override bool IsValidUnionMember => true;

        /// <summary>
        /// Structs can be part of vectors.
        /// </summary>
        public override bool IsValidVectorMember => true;

        /// <summary>
        /// Structs can't be keys of sorted vectors.
        /// </summary>
        public override bool IsValidSortedVectorKey => false;

        /// <summary>
        /// Structs are written inline.
        /// </summary>
        public override bool SerializesInline => true;

        /// <summary>
        /// Gets the members of this struct.
        /// </summary>
        public IReadOnlyList<StructMemberModel> Members => this.memberTypes;

        public override CodeGeneratedMethod CreateGetMaxSizeMethodBody(GetMaxSizeCodeGenContext context)
        {
            return new CodeGeneratedMethod
            {
                MethodBody = $"return {this.MaxInlineSize};",
            };
        }

        public override CodeGeneratedMethod CreateParseMethodBody(ParserCodeGenContext context)
        {
            if (this.ClrType.IsValueType)
            {
                return this.CreateStructParseBody(context);
            }
            else
            {
                return this.CreateClassParseMethodBody(context);
            }
        }

        private CodeGeneratedMethod CreateStructParseBody(ParserCodeGenContext context)
        {
            var propertyStatements = new List<string>();
            for (int i = 0; i < this.Members.Count; ++i)
            {
                var member = this.Members[i];
                propertyStatements.Add($@"
                    item.{member.PropertyInfo.Name} = {context.MethodNameMap[member.PropertyInfo.PropertyType]}<{context.InputBufferTypeName}>(
                        {context.InputBufferVariableName}), 
                        ({context.OffsetVariableName} + {member.Offset});");
            }

            // For little endian architectures, we can do the equivalent of a reinterpret_cast operation.
            string body = $@"
            if (BitConverter.IsLittleEndian)
            {{
                var mem = {context.InputBufferVariableName}.{nameof(IInputBuffer.GetReadOnlyByteMemory)}({context.OffsetVariableName}, {this.inlineSize});
                return {nameof(MemoryMarshal)}.{nameof(MemoryMarshal.Cast)}<byte, {CSharpHelpers.GetCompilableTypeName(this.ClrType)}>(mem.Span)[0];
            }}
            else
            {{
                var item = default({CSharpHelpers.GetCompilableTypeName(this.ClrType)};
                {string.Join("\r\n", propertyStatements)}
                return item;
            }}
";

            return new CodeGeneratedMethod { MethodBody = body };
        }

        private CodeGeneratedMethod CreateClassParseMethodBody(ParserCodeGenContext context)
        {
            // We have to implement two items: The table class and the overall "read" method.
            // Let's start with the read method.
            string className = "structReader_" + Guid.NewGuid().ToString("n");

            string classDefinition;
            // Implement the class
            {
                // Build up a list of property overrides.
                var propertyOverrides = new List<GeneratedProperty>();
                for (int index = 0; index < this.Members.Count; ++index)
                {
                    var value = this.Members[index];
                    PropertyInfo propertyInfo = value.PropertyInfo;
                    Type propertyType = propertyInfo.PropertyType;

                    GeneratedProperty generatedProperty = new GeneratedProperty(context.Options, index, propertyInfo);

                    var propContext = context.With(offset: $"({context.OffsetVariableName} + {value.Offset})", inputBuffer: "buffer");

                    // These are always inline as they are only invoked from one place.
                    generatedProperty.ReadValueMethodDefinition =
$@"
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    private static {CSharpHelpers.GetCompilableTypeName(propertyType)} {generatedProperty.ReadValueMethodName}({context.InputBufferTypeName} buffer, int offset)
                    {{
                        return {propContext.GetParseInvocation(propertyType)};
                    }}
";

                    propertyOverrides.Add(generatedProperty);
                }

                classDefinition = CSharpHelpers.CreateDeserializeClass(
                    className,
                    this.ClrType,
                    propertyOverrides,
                    context.Options);
            }

            return new CodeGeneratedMethod
            {
                ClassDefinition = classDefinition,
                MethodBody = $"return new {className}<{context.InputBufferTypeName}>({context.InputBufferVariableName}, {context.OffsetVariableName});",
            };
        }

        public override CodeGeneratedMethod CreateSerializeMethodBody(SerializationCodeGenContext context)
        {
            return this.ClrType.IsValueType
                ? this.CreateStructSerializeBody(context)
                : this.CreateClassSerializeMethodBody(context);
        }

        private CodeGeneratedMethod CreateStructSerializeBody(SerializationCodeGenContext context)
        {
            var propertyStatements = new List<string>();
            for (int i = 0; i < this.Members.Count; ++i)
            {
                var member = this.Members[i];
                propertyStatements.Add($@"
                    {context.MethodNameMap[member.PropertyInfo.PropertyType]}(
                        {context.SpanWriterVariableName}, 
                        {context.SpanVariableName},
                        {context.ValueVariableName}.{member.PropertyInfo.Name},
                        {context.OffsetVariableName} + {member.Offset},
                        {context.SerializationContextVariableName});");
            }

            // For little endian architectures, we can do the equivalent of a reinterpret_cast operation.
            string body = $@"
            if (BitConverter.IsLittleEndian)
            {{
                var tempSpan = {nameof(MemoryMarshal)}.{nameof(MemoryMarshal.Cast)}<byte, {CSharpHelpers.GetCompilableTypeName(this.ClrType)}>({context.SpanVariableName}.Slice({context.OffsetVariableName}, {this.inlineSize}));
                tempSpan[0] = {context.ValueVariableName};
            }}
            else
            {{
                {string.Join("\r\n", propertyStatements)}
            }}
";

            return new CodeGeneratedMethod { MethodBody = body };
        }

        private CodeGeneratedMethod CreateClassSerializeMethodBody(SerializationCodeGenContext context)
        {
            List<string> body = new List<string>();
            for (int i = 0; i < this.Members.Count; ++i)
            {
                var memberInfo = this.Members[i];

                string propertyAccessor = $"{context.ValueVariableName}.{memberInfo.PropertyInfo.Name}";
                if (!memberInfo.ItemTypeModel.ClrType.IsValueType)
                {
                    // Force members of structs to be non-null.
                    propertyAccessor += $" ?? new {CSharpHelpers.GetCompilableTypeName(memberInfo.ItemTypeModel.ClrType)}()";
                }

                var propContext = context.With(
                    offsetVariableName: $"({memberInfo.Offset} + {context.OffsetVariableName})",
                    valueVariableName: $"({propertyAccessor})");

                body.Add(propContext.GetSerializeInvocation(memberInfo.ItemTypeModel.ClrType) + ";");
            }

            return new CodeGeneratedMethod
            {
                MethodBody = string.Join("\r\n", body)
            };
        }

        public override string GetThrowIfNullInvocation(string itemVariableName)
        {
            return $"{nameof(SerializationHelpers)}.{nameof(SerializationHelpers.EnsureNonNull)}({itemVariableName})";
        }

        public override void Initialize()
        {
            var structAttribute = this.ClrType.GetCustomAttribute<FlatBufferStructAttribute>();
            if (structAttribute == null)
            {
                throw new InvalidFlatBufferDefinitionException($"Can't create struct type model from type {this.ClrType.Name} because it does not have a [FlatBufferStruct] attribute.");
            }

            var properties = this.ClrType
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(x => new
                {
                    Property = x,
                    Attribute = x.GetCustomAttribute<FlatBufferItemAttribute>(),
                    ExplicitOffsetAttribute = x.GetCustomAttribute<FieldOffsetAttribute>(),
                })
                .Where(x => x.Attribute != null)
                .OrderBy(x => x.Attribute.Index);

            ushort expectedIndex = 0;
            this.inlineSize = 0;

            foreach (var item in properties)
            {
                var propertyAttribute = item.Attribute;
                var property = item.Property;

                if (propertyAttribute.Deprecated)
                {
                    throw new InvalidFlatBufferDefinitionException($"FlatBuffer struct {this.ClrType.Name} may not have deprecated properties");
                }

                ushort index = propertyAttribute.Index;
                if (index != expectedIndex)
                {
                    throw new InvalidFlatBufferDefinitionException($"FlatBuffer struct {this.ClrType.Name} does not declare an item with index {expectedIndex}. Structs must have sequenential indexes starting at 0.");
                }
                
                if (!object.ReferenceEquals(propertyAttribute.DefaultValue, null))
                {
                    throw new InvalidFlatBufferDefinitionException($"FlatBuffer struct {this.ClrType.Name} declares default value on index {expectedIndex}. Structs may not have default values.");
                }

                expectedIndex++;
                ITypeModel propertyModel = this.typeModelContainer.CreateTypeModel(property.PropertyType);

                if (!propertyModel.IsValidStructMember || propertyModel.PhysicalLayout.Length > 1)
                {
                    throw new InvalidFlatBufferDefinitionException($"Struct property {property.Name} with type {property.PropertyType.Name} cannot be part of a flatbuffer struct.");
                }

                if (this.ClrType.IsValueType && !propertyModel.ClrType.IsValueType)
                {
                    throw new InvalidFlatBufferDefinitionException($"Struct {this.ClrType.Name} property {property.Name} must be a value type if the struct is a value type.");
                }

                int propertySize = propertyModel.PhysicalLayout[0].InlineSize;
                int propertyAlignment = propertyModel.PhysicalLayout[0].Alignment;
                this.maxAlignment = Math.Max(propertyAlignment, this.maxAlignment);

                // Pad for alignment.
                this.inlineSize += SerializationHelpers.GetAlignmentError(this.inlineSize, propertyAlignment);

                StructMemberModel model = new StructMemberModel(
                    propertyModel,
                    property,
                    index,
                    this.inlineSize);

                this.memberTypes.Add(model);

                if (this.ClrType.IsValueType && item.ExplicitOffsetAttribute?.Value != this.inlineSize)
                {
                    throw new InvalidFlatBufferDefinitionException($"Struct '{this.ClrType.Name}' property '{property.Name}' defines invalid [FieldOffset attribute. Expected [FieldOffset({this.inlineSize})].");
                }

                this.inlineSize += propertyModel.PhysicalLayout[0].InlineSize;
            }

            // Validate the struct type after all the alignment business.
            if (this.ClrType.IsValueType)
            {
                var physicalLayout = this.ClrType.GetCustomAttribute<StructLayoutAttribute>();
                if (physicalLayout?.Value != LayoutKind.Explicit ||
                    physicalLayout?.Pack != this.maxAlignment ||
                    physicalLayout?.Size != this.inlineSize)
                {
                    throw new InvalidFlatBufferDefinitionException($"Struct {this.ClrType.Name} must define a [StructLayout(LayoutKind.Explicit, Pack = {this.maxAlignment}, Size = {this.inlineSize})] attribute.");
                }
            }
            else
            {
                TableTypeModel.EnsureClassCanBeInheritedByOutsideAssembly(this.ClrType, out var ctor);
            }
        }

        public override void TraverseObjectGraph(HashSet<Type> seenTypes)
        {
            seenTypes.Add(this.ClrType);
            foreach (var member in this.memberTypes)
            {
                if (seenTypes.Add(member.ItemTypeModel.ClrType))
                {
                    member.ItemTypeModel.TraverseObjectGraph(seenTypes);
                }
            }
        }
    }
}
