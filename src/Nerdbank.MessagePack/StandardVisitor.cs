﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable NBMsgPackAsync

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft;
using PolyType.Utilities;

namespace Nerdbank.MessagePack;

/// <summary>
/// A <see cref="TypeShapeVisitor"/> that produces <see cref="MessagePackConverter{T}"/> instances for each type shape it visits.
/// </summary>
internal class StandardVisitor : TypeShapeVisitor, ITypeShapeFunc
{
	private static readonly InterningStringConverter InterningStringConverter = new();
	private static readonly MessagePackConverter<string> ReferencePreservingInterningStringConverter = InterningStringConverter.WrapWithReferencePreservation();

	private readonly ConverterCache owner;
	private readonly TypeGenerationContext context;

	/// <summary>
	/// Initializes a new instance of the <see cref="StandardVisitor"/> class.
	/// </summary>
	/// <param name="owner">The serializer that created this instance. Usable for obtaining settings that may influence the generated converter.</param>
	/// <param name="context">Context for a generation of a particular data model.</param>
	internal StandardVisitor(ConverterCache owner, TypeGenerationContext context)
	{
		this.owner = owner;
		this.context = context;
		this.OutwardVisitor = this;
	}

	/// <summary>
	/// Gets or sets the visitor that will be used to generate converters for new types that are encountered.
	/// </summary>
	/// <value>Defaults to <see langword="this" />.</value>
	/// <remarks>
	/// This may be changed to a wrapping visitor implementation to implement features such as reference preservation.
	/// </remarks>
	internal TypeShapeVisitor OutwardVisitor { get; set; }

	/// <inheritdoc/>
	object? ITypeShapeFunc.Invoke<T>(ITypeShape<T> typeShape, object? state)
	{
		// Check if the type has a custom converter.
		if (this.owner.TryGetUserDefinedConverter(typeShape, out MessagePackConverter<T>? userDefinedConverter))
		{
			return userDefinedConverter;
		}

		if (this.owner.InternStrings && typeof(T) == typeof(string))
		{
			return this.owner.PreserveReferences != ReferencePreservationMode.Off ? ReferencePreservingInterningStringConverter : InterningStringConverter;
		}

		// Check if the type has a built-in converter.
		if (PrimitiveConverterLookup.TryGetPrimitiveConverter(this.owner.PreserveReferences, out MessagePackConverter<T>? defaultConverter))
		{
			return defaultConverter;
		}

		// Otherwise, build a converter using the visitor.
		return typeShape.Accept(this.OutwardVisitor, state);
	}

	/// <inheritdoc/>
	public override object? VisitObject<T>(IObjectTypeShape<T> objectShape, object? state = null)
	{
		if (this.GetCustomConverter(objectShape, objectShape.AttributeProvider) is MessagePackConverter<T> customConverter)
		{
			return customConverter;
		}

		IConstructorShape? ctorShape = objectShape.Constructor;

		Dictionary<string, IParameterShape>? ctorParametersByName = null;
		if (ctorShape is not null)
		{
			ctorParametersByName = new(StringComparer.Ordinal);
			foreach (IParameterShape ctorParameter in ctorShape.Parameters)
			{
				// Keep the one with the Kind that we prefer.
				if (ctorParameter.Kind == ParameterKind.MethodParameter)
				{
					ctorParametersByName[ctorParameter.Name] = ctorParameter;
				}
				else if (!ctorParametersByName.ContainsKey(ctorParameter.Name))
				{
					ctorParametersByName.Add(ctorParameter.Name, ctorParameter);
				}
			}
		}

		List<SerializableProperty<T>>? serializable = null;
		List<DeserializableProperty<T>>? deserializable = null;
		List<(string Name, PropertyAccessors<T> Accessors)?>? propertyAccessors = null;
		DirectPropertyAccess<T, UnusedDataPacket>? unusedDataPropertyAccess = null;
		int propertyIndex = -1;
		foreach (IPropertyShape property in objectShape.Properties)
		{
			if (property is IPropertyShape<T, UnusedDataPacket> unusedDataProperty)
			{
				if (unusedDataPropertyAccess is null)
				{
					unusedDataPropertyAccess = new DirectPropertyAccess<T, UnusedDataPacket>(unusedDataProperty.HasSetter ? unusedDataProperty.GetSetter() : null, unusedDataProperty.HasGetter ? unusedDataProperty.GetGetter() : null);
				}
				else
				{
					throw new MessagePackSerializationException($"The type {objectShape.Type.FullName} has multiple properties of type {typeof(UnusedDataPacket).FullName}. Only one such property is allowed.");
				}

				continue;
			}

			propertyIndex++;
			string propertyName = this.owner.GetSerializedPropertyName(property.Name, property.AttributeProvider);

			IParameterShape? matchingConstructorParameter = null;
			ctorParametersByName?.TryGetValue(property.Name, out matchingConstructorParameter);

			if (property.Accept(this, matchingConstructorParameter) is PropertyAccessors<T> accessors)
			{
				KeyAttribute? keyAttribute = (KeyAttribute?)property.AttributeProvider?.GetCustomAttributes(typeof(KeyAttribute), false).FirstOrDefault();
				if (keyAttribute is not null || this.owner.PerfOverSchemaStability || objectShape.IsTupleType)
				{
					propertyAccessors ??= new();
					int index = keyAttribute?.Index ?? propertyIndex;
					while (propertyAccessors.Count <= index)
					{
						propertyAccessors.Add(null);
					}

					propertyAccessors[index] = (propertyName, accessors);
				}
				else
				{
					serializable ??= new();
					deserializable ??= new();

					StringEncoding.GetEncodedStringBytes(propertyName, out ReadOnlyMemory<byte> utf8Bytes, out ReadOnlyMemory<byte> msgpackEncoded);
					if (accessors.MsgPackWriters is var (serialize, serializeAsync))
					{
						serializable.Add(new(propertyName, msgpackEncoded, serialize, serializeAsync, accessors.Converter, accessors.ShouldSerialize, property));
					}

					if (accessors.MsgPackReaders is var (deserialize, deserializeAsync))
					{
						deserializable.Add(new(property.Name, utf8Bytes, deserialize, deserializeAsync, accessors.Converter, property.Position));
					}
				}
			}
		}

		MessagePackConverter<T> converter;
		if (propertyAccessors is not null)
		{
			if (serializable is { Count: > 0 })
			{
				// Members with and without KeyAttribute have been detected as intended for serialization. These two worlds are incompatible.
				throw new MessagePackSerializationException(PrepareExceptionMessage());

				string PrepareExceptionMessage()
				{
					// Avoid use of Linq methods since it will lead to native code gen that closes generics over user types.
					StringBuilder builder = new();
					builder.Append($"The type {objectShape.Type.FullName} has fields/properties that are candidates for serialization but are inconsistently attributed with {nameof(KeyAttribute)}.\nMembers with the attribute: ");
					bool first = true;
					foreach ((string Name, PropertyAccessors<T> Accessors)? a in propertyAccessors)
					{
						if (a is not null)
						{
							if (!first)
							{
								builder.Append(", ");
							}

							first = false;
							builder.Append(a.Value.Name);
						}
					}

					builder.Append("\nMembers without the attribute: ");
					first = true;
					foreach (SerializableProperty<T> p in serializable)
					{
						if (!first)
						{
							builder.Append(", ");
						}

						first = false;
						builder.Append(p.Name);
					}

					return builder.ToString();
				}
			}

			ArrayConstructorVisitorInputs<T> inputs = new(propertyAccessors, unusedDataPropertyAccess);
			converter = ctorShape is not null
				? (MessagePackConverter<T>)ctorShape.Accept(this, inputs)!
				: new ObjectArrayConverter<T>(inputs.GetJustAccessors(), unusedDataPropertyAccess, null, objectShape.Properties, this.owner.SerializeDefaultValues);
		}
		else
		{
			SpanDictionary<byte, DeserializableProperty<T>>? propertyReaders = deserializable?
				.ToSpanDictionary(
					p => p.PropertyNameUtf8,
					ByteSpanEqualityComparer.Ordinal);

			MapSerializableProperties<T> serializableMap = new(serializable?.ToArray());
			MapDeserializableProperties<T> deserializableMap = new(propertyReaders);
			if (ctorShape is not null)
			{
				MapConstructorVisitorInputs<T> inputs = new(serializableMap, deserializableMap, ctorParametersByName!, unusedDataPropertyAccess);
				converter = (MessagePackConverter<T>)ctorShape.Accept(this, inputs)!;
			}
			else
			{
				Func<T>? ctor = typeof(T) == typeof(object) ? (Func<T>)(object)new Func<object>(() => new object()) : null;
				converter = new ObjectMapConverter<T>(
					serializableMap,
					deserializableMap,
					unusedDataPropertyAccess,
					ctor,
					objectShape.Properties,
					this.owner.SerializeDefaultValues);
			}
		}

		// Test IsValueType before calling DiscoverUnionTypes so that the native compiler
		// does not have to generate a SubTypes<T> for value types which will never be used.
		return !typeof(T).IsValueType && this.DiscoverUnionTypes(objectShape, converter) is { } unionTypes
			? new UnionConverter<T>(converter, unionTypes)
			: converter;
	}

	/// <inheritdoc/>
	public override object? VisitUnion<TUnion>(IUnionTypeShape<TUnion> unionShape, object? state = null)
	{
		MessagePackConverter<TUnion> baseTypeConverter = (MessagePackConverter<TUnion>)unionShape.BaseType.Accept(this)!;

		if (baseTypeConverter is UnionConverter<TUnion>)
		{
			// A runtime mapping *and* attributes are defined for the same base type.
			// The runtime mapping has already been applied and that trumps attributes.
			// Just return the union converter we created for the runtime mapping to avoid
			// double-nesting.
			return baseTypeConverter;
		}

		// Runtime mapping overrides attributes.
		if (!(unionShape.BaseType is IObjectTypeShape<TUnion> baseObjectShape && this.DiscoverUnionTypes(baseObjectShape, baseTypeConverter) is { } subTypes))
		{
			Getter<TUnion, int> getUnionCaseIndex = unionShape.GetGetUnionCaseIndex();
			Dictionary<int, MessagePackConverter> deserializerByIntAlias = new(unionShape.UnionCases.Count);
			List<(DerivedTypeIdentifier Alias, MessagePackConverter Converter, ITypeShape Shape)> serializers = new(unionShape.UnionCases.Count);
			KeyValuePair<int, MessagePackConverter<TUnion>>[] unionCases = unionShape.UnionCases
				.Select(unionCase =>
				{
					bool useTag = unionCase.IsTagSpecified || this.owner.PerfOverSchemaStability;
					DerivedTypeIdentifier alias = useTag ? new(unionCase.Tag) : new(unionCase.Name);
					var caseConverter = (MessagePackConverter<TUnion>)unionCase.Accept(this, null)!;
					deserializerByIntAlias.Add(unionCase.Tag, caseConverter);
					serializers.Add((alias, caseConverter, unionCase.Type));

					return new KeyValuePair<int, MessagePackConverter<TUnion>>(unionCase.Tag, caseConverter);
				})
				.ToArray();
			subTypes = new()
			{
				DeserializersByIntAlias = deserializerByIntAlias.ToFrozenDictionary(),
				DeserializersByStringAlias = serializers.Where(v => v.Alias.Type == DerivedTypeIdentifier.AliasType.String).ToSpanDictionary(
					p => p.Alias.Utf8Alias,
					p => p.Converter,
					ByteSpanEqualityComparer.Ordinal),
				Serializers = serializers.ToFrozenSet(),
				TryGetSerializer = (ref TUnion value) => getUnionCaseIndex(ref value) is int idx && idx >= 0 ? (serializers[idx].Alias, serializers[idx].Converter) : null,
			};
		}

		return new UnionConverter<TUnion>(baseTypeConverter, subTypes);
	}

	/// <inheritdoc/>
	public override object? VisitUnionCase<TUnionCase, TUnion>(IUnionCaseShape<TUnionCase, TUnion> unionCaseShape, object? state = null)
	{
		// NB: don't use the cached converter for TUnionCase, as it might equal TUnion.
		var caseConverter = (MessagePackConverter<TUnionCase>)unionCaseShape.Type.Accept(this)!;
		return new UnionCaseConverter<TUnionCase, TUnion>(caseConverter);
	}

	/// <inheritdoc/>
	public override object? VisitProperty<TDeclaringType, TPropertyType>(IPropertyShape<TDeclaringType, TPropertyType> propertyShape, object? state = null)
	{
		IParameterShape? constructorParameterShape = (IParameterShape?)state;

		MessagePackConverter<TPropertyType> converter =
			this.GetCustomConverter(propertyShape.PropertyType, propertyShape.AttributeProvider) ??
			this.GetConverter(propertyShape.PropertyType, propertyShape.AttributeProvider);

		(SerializeProperty<TDeclaringType>, SerializePropertyAsync<TDeclaringType>)? msgpackWriters = null;
		Func<TDeclaringType, bool>? shouldSerialize = null;
		if (propertyShape.HasGetter)
		{
			Getter<TDeclaringType, TPropertyType> getter = propertyShape.GetGetter();
			EqualityComparer<TPropertyType> eq = EqualityComparer<TPropertyType>.Default;

			if (this.owner.SerializeDefaultValues != SerializeDefaultValuesPolicy.Always)
			{
				// Test for value-independent flags that would indicate this property must always be serialized.
				bool alwaysSerialize =
					((this.owner.SerializeDefaultValues & SerializeDefaultValuesPolicy.ValueTypes) == SerializeDefaultValuesPolicy.ValueTypes && typeof(TPropertyType).IsValueType) ||
					((this.owner.SerializeDefaultValues & SerializeDefaultValuesPolicy.ReferenceTypes) == SerializeDefaultValuesPolicy.ReferenceTypes && !typeof(TPropertyType).IsValueType) ||
					((this.owner.SerializeDefaultValues & SerializeDefaultValuesPolicy.Required) == SerializeDefaultValuesPolicy.Required && constructorParameterShape is { IsRequired: true });

				if (alwaysSerialize)
				{
					shouldSerialize = static obj => true;
				}
				else
				{
					// The only possibility for serializing the property that remains is that it has a non-default value.
					TPropertyType? defaultValue = default;
					if (constructorParameterShape?.HasDefaultValue is true)
					{
						defaultValue = (TPropertyType?)constructorParameterShape.DefaultValue;
					}
					else if (propertyShape.AttributeProvider?.GetCustomAttributes(typeof(System.ComponentModel.DefaultValueAttribute), true).FirstOrDefault() is System.ComponentModel.DefaultValueAttribute { Value: TPropertyType attributeDefaultValue })
					{
						defaultValue = attributeDefaultValue;
					}

					shouldSerialize = obj => !eq.Equals(getter(ref obj), defaultValue!);
				}
			}

			SerializeProperty<TDeclaringType> serialize = (in TDeclaringType container, ref MessagePackWriter writer, SerializationContext context) =>
			{
				// Workaround https://github.com/eiriktsarpalis/PolyType/issues/46.
				// We get significantly improved usability in the API if we use the `in` modifier on the Serialize method
				// instead of `ref`. And since serialization should fundamentally be a read-only operation, this *should* be safe.
				TPropertyType? value = getter(ref Unsafe.AsRef(in container));
				converter.Write(ref writer, value, context);
			};
			SerializePropertyAsync<TDeclaringType> serializeAsync = (TDeclaringType container, MessagePackAsyncWriter writer, SerializationContext context)
				=> converter.WriteAsync(writer, getter(ref container), context);
			msgpackWriters = (serialize, serializeAsync);
		}

		bool suppressIfNoConstructorParameter = true;
		(DeserializeProperty<TDeclaringType>, DeserializePropertyAsync<TDeclaringType>)? msgpackReaders = null;
		if (propertyShape.HasSetter)
		{
			Setter<TDeclaringType, TPropertyType> setter = propertyShape.GetSetter();
			DeserializeProperty<TDeclaringType> deserialize = (ref TDeclaringType container, ref MessagePackReader reader, SerializationContext context) => setter(ref container, converter.Read(ref reader, context)!);
			DeserializePropertyAsync<TDeclaringType> deserializeAsync = async (TDeclaringType container, MessagePackAsyncReader reader, SerializationContext context) =>
			{
				setter(ref container, (await converter.ReadAsync(reader, context).ConfigureAwait(false))!);
				return container;
			};
			msgpackReaders = (deserialize, deserializeAsync);
			suppressIfNoConstructorParameter = false;
		}
		else if (propertyShape.HasGetter && converter is IDeserializeInto<TPropertyType> inflater)
		{
			// The property has no setter, but it has a getter and the property type is a collection.
			// So we'll assume the declaring type initializes the collection in its constructor,
			// and we'll just deserialize into it.
			suppressIfNoConstructorParameter = false;
			Getter<TDeclaringType, TPropertyType> getter = propertyShape.GetGetter();
			DeserializeProperty<TDeclaringType> deserialize = (ref TDeclaringType container, ref MessagePackReader reader, SerializationContext context) =>
			{
				if (reader.TryReadNil())
				{
					// No elements to read. A null collection in msgpack doesn't let us set the collection to null, so just return.
					return;
				}

				TPropertyType collection = getter(ref container);
				inflater.DeserializeInto(ref reader, ref collection, context);
			};
			DeserializePropertyAsync<TDeclaringType> deserializeAsync = async (TDeclaringType container, MessagePackAsyncReader reader, SerializationContext context) =>
			{
				MessagePackStreamingReader streamingReader = reader.CreateStreamingReader();
				bool isNil;
				while (streamingReader.TryReadNil(out isNil).NeedsMoreBytes())
				{
					streamingReader = new(await streamingReader.FetchMoreBytesAsync().ConfigureAwait(false));
				}

				if (!isNil)
				{
					TPropertyType collection = propertyShape.GetGetter()(ref container);
					await inflater.DeserializeIntoAsync(reader, collection, context).ConfigureAwait(false);
				}

				return container;
			};
			msgpackReaders = (deserialize, deserializeAsync);
		}

		return suppressIfNoConstructorParameter && constructorParameterShape is null
			? null
			: new PropertyAccessors<TDeclaringType>(msgpackWriters, msgpackReaders, converter, shouldSerialize, propertyShape);
	}

	/// <inheritdoc/>
	public override object? VisitConstructor<TDeclaringType, TArgumentState>(IConstructorShape<TDeclaringType, TArgumentState> constructorShape, object? state = null)
	{
		switch (state)
		{
			case MapConstructorVisitorInputs<TDeclaringType> inputs:
				{
					if (constructorShape.Parameters.Count == 0)
					{
						return new ObjectMapConverter<TDeclaringType>(
							inputs.Serializers,
							inputs.Deserializers,
							inputs.UnusedDataProperty,
							constructorShape.GetDefaultConstructor(),
							constructorShape.DeclaringType.Properties,
							this.owner.SerializeDefaultValues);
					}

					List<SerializableProperty<TDeclaringType>> propertySerializers = inputs.Serializers.Properties.Span.ToList();

					var spanDictContent = new KeyValuePair<ReadOnlyMemory<byte>, DeserializableProperty<TArgumentState>>[inputs.ParametersByName.Count];
					int i = 0;
					foreach (KeyValuePair<string, IParameterShape> p in inputs.ParametersByName)
					{
						ICustomAttributeProvider? propertyAttributeProvider = constructorShape.DeclaringType.Properties.FirstOrDefault(prop => prop.Name == p.Value.Name)?.AttributeProvider;
						var prop = (DeserializableProperty<TArgumentState>)p.Value.Accept(this)!;
						string name = this.owner.GetSerializedPropertyName(p.Value.Name, propertyAttributeProvider);
						spanDictContent[i++] = new(Encoding.UTF8.GetBytes(name), prop);
					}

					SpanDictionary<byte, DeserializableProperty<TArgumentState>> parameters = new(spanDictContent, ByteSpanEqualityComparer.Ordinal);

					MapSerializableProperties<TDeclaringType> serializeable = inputs.Serializers;
					serializeable.Properties = propertySerializers.ToArray();
					return new ObjectMapWithNonDefaultCtorConverter<TDeclaringType, TArgumentState>(
						serializeable,
						constructorShape.GetArgumentStateConstructor(),
						inputs.UnusedDataProperty,
						constructorShape.GetParameterizedConstructor(),
						new MapDeserializableProperties<TArgumentState>(parameters),
						constructorShape.Parameters,
						this.owner.SerializeDefaultValues,
						this.owner.DeserializeDefaultValues);
				}

			case ArrayConstructorVisitorInputs<TDeclaringType> inputs:
				{
					if (constructorShape.Parameters.Count == 0)
					{
						return new ObjectArrayConverter<TDeclaringType>(inputs.GetJustAccessors(), inputs.UnusedDataProperty, constructorShape.GetDefaultConstructor(), constructorShape.DeclaringType.Properties, this.owner.SerializeDefaultValues);
					}

					Dictionary<string, int> propertyIndexesByName = new(StringComparer.Ordinal);
					for (int i = 0; i < inputs.Properties.Count; i++)
					{
						if (inputs.Properties[i] is { } property)
						{
							propertyIndexesByName[property.Name] = i;
						}
					}

					DeserializableProperty<TArgumentState>?[] parameters = new DeserializableProperty<TArgumentState>?[inputs.Properties.Count];
					foreach (IParameterShape parameter in constructorShape.Parameters)
					{
						if (parameter is IParameterShape<TArgumentState, UnusedDataPacket>)
						{
							continue;
						}

						int index = propertyIndexesByName[parameter.Name];
						parameters[index] = (DeserializableProperty<TArgumentState>)parameter.Accept(this)!;
					}

					return new ObjectArrayWithNonDefaultCtorConverter<TDeclaringType, TArgumentState>(
						inputs.GetJustAccessors(),
						inputs.UnusedDataProperty,
						constructorShape.GetArgumentStateConstructor(),
						constructorShape.GetParameterizedConstructor(),
						parameters,
						constructorShape.Parameters,
						this.owner.SerializeDefaultValues,
						this.owner.DeserializeDefaultValues);
				}

			default:
				throw new NotSupportedException("Unsupported state.");
		}
	}

	/// <inheritdoc/>
	public override object? VisitParameter<TArgumentState, TParameterType>(IParameterShape<TArgumentState, TParameterType> parameterShape, object? state = null)
	{
		MessagePackConverter<TParameterType> converter = this.GetConverter(parameterShape.ParameterType, parameterShape.AttributeProvider);

		Setter<TArgumentState, TParameterType> setter = parameterShape.GetSetter();

		DeserializeProperty<TArgumentState> read;
		DeserializePropertyAsync<TArgumentState> readAsync;
		bool throwOnNull =
			(this.owner.DeserializeDefaultValues & DeserializeDefaultValuesPolicy.AllowNullValuesForNonNullableProperties) != DeserializeDefaultValuesPolicy.AllowNullValuesForNonNullableProperties
			&& parameterShape.IsNonNullable
			&& !typeof(TParameterType).IsValueType;
		if (throwOnNull)
		{
			static Exception NewDisallowedDeserializedNullValueException(IParameterShape parameter) => new MessagePackSerializationException($"The parameter {parameter.Name} is non-nullable, but the deserialized value was null.") { Code = MessagePackSerializationException.ErrorCode.DisallowedNullValue };
			read = (ref TArgumentState state, ref MessagePackReader reader, SerializationContext context) =>
			{
				ThrowIfAlreadyAssigned(state, parameterShape.Position, parameterShape.Name);
				setter(ref state, converter.Read(ref reader, context) ?? throw NewDisallowedDeserializedNullValueException(parameterShape));
			};
			readAsync = async (TArgumentState state, MessagePackAsyncReader reader, SerializationContext context) =>
			{
				ThrowIfAlreadyAssigned(state, parameterShape.Position, parameterShape.Name);
				setter(ref state, (await converter.ReadAsync(reader, context).ConfigureAwait(false)) ?? throw NewDisallowedDeserializedNullValueException(parameterShape));
				return state;
			};
		}
		else
		{
			read = (ref TArgumentState state, ref MessagePackReader reader, SerializationContext context) =>
			{
				ThrowIfAlreadyAssigned(state, parameterShape.Position, parameterShape.Name);
				setter(ref state, converter.Read(ref reader, context)!);
			};
			readAsync = async (TArgumentState state, MessagePackAsyncReader reader, SerializationContext context) =>
			{
				ThrowIfAlreadyAssigned(state, parameterShape.Position, parameterShape.Name);
				setter(ref state, (await converter.ReadAsync(reader, context).ConfigureAwait(false))!);
				return state;
			};
		}

		return new DeserializableProperty<TArgumentState>(
			parameterShape.Name,
			StringEncoding.UTF8.GetBytes(parameterShape.Name),
			read,
			readAsync,
			converter,
			parameterShape.Position);
	}

	/// <inheritdoc/>
	public override object? VisitOptional<TOptional, TElement>(IOptionalTypeShape<TOptional, TElement> optionalShape, object? state = null)
		=> new OptionalConverter<TOptional, TElement>(this.GetConverter(optionalShape.ElementType), optionalShape.GetDeconstructor(), optionalShape.GetNoneConstructor(), optionalShape.GetSomeConstructor());

	/// <inheritdoc/>
	public override object? VisitDictionary<TDictionary, TKey, TValue>(IDictionaryTypeShape<TDictionary, TKey, TValue> dictionaryShape, object? state = null)
	{
		MemberConverterInfluence? memberInfluence = state as MemberConverterInfluence;

		// Serialization functions.
		MessagePackConverter<TKey> keyConverter = this.GetConverter(dictionaryShape.KeyType);
		MessagePackConverter<TValue> valueConverter = this.GetConverter(dictionaryShape.ValueType);
		Func<TDictionary, IReadOnlyDictionary<TKey, TValue>> getReadable = dictionaryShape.GetGetDictionary();

		// Deserialization functions.
		return dictionaryShape.ConstructionStrategy switch
		{
			CollectionConstructionStrategy.None => new DictionaryConverter<TDictionary, TKey, TValue>(getReadable, keyConverter, valueConverter),
			CollectionConstructionStrategy.Mutable => new MutableDictionaryConverter<TDictionary, TKey, TValue>(getReadable, keyConverter, valueConverter, dictionaryShape.GetInserter(DictionaryInsertionMode.Throw), dictionaryShape.GetDefaultConstructor(), this.GetCollectionOptions(dictionaryShape, memberInfluence)),
			CollectionConstructionStrategy.Parameterized => new ImmutableDictionaryConverter<TDictionary, TKey, TValue>(getReadable, keyConverter, valueConverter, dictionaryShape.GetParameterizedConstructor(), this.GetCollectionOptions(dictionaryShape, memberInfluence)),
			_ => throw new NotSupportedException($"Unrecognized dictionary pattern: {typeof(TDictionary).Name}"),
		};
	}

	/// <inheritdoc/>
	public override object? VisitEnumerable<TEnumerable, TElement>(IEnumerableTypeShape<TEnumerable, TElement> enumerableShape, object? state = null)
	{
		MemberConverterInfluence? memberInfluence = state as MemberConverterInfluence;

		// Serialization functions.
		MessagePackConverter<TElement> elementConverter = this.GetConverter(enumerableShape.ElementType);

		if (enumerableShape.Type.IsArray)
		{
			MessagePackConverter<TEnumerable>? converter;
			if (enumerableShape.Rank > 1)
			{
#if NET
				return this.owner.MultiDimensionalArrayFormat switch
				{
					MultiDimensionalArrayFormat.Nested => new ArrayWithNestedDimensionsConverter<TEnumerable, TElement>(elementConverter, enumerableShape.Rank),
					MultiDimensionalArrayFormat.Flat => new ArrayWithFlattenedDimensionsConverter<TEnumerable, TElement>(elementConverter),
					_ => throw new NotSupportedException(),
				};
#else
				throw PolyfillExtensions.ThrowNotSupportedOnNETFramework();
#endif
			}
#if NET
			else if (!this.owner.DisableHardwareAcceleration &&
				enumerableShape.ConstructionStrategy == CollectionConstructionStrategy.Parameterized &&
				HardwareAccelerated.TryGetConverter<TEnumerable, TElement>(out converter))
			{
				return converter;
			}
#endif
			else if (enumerableShape.ConstructionStrategy == CollectionConstructionStrategy.Parameterized &&
				ArraysOfPrimitivesConverters.TryGetConverter(enumerableShape.GetGetEnumerable(), enumerableShape.GetParameterizedConstructor(), out converter))
			{
				return converter;
			}
			else
			{
				return new ArrayConverter<TElement>(elementConverter);
			}
		}

		Func<TEnumerable, IEnumerable<TElement>>? getEnumerable = enumerableShape.IsAsyncEnumerable ? null : enumerableShape.GetGetEnumerable();
		return enumerableShape.ConstructionStrategy switch
		{
			CollectionConstructionStrategy.None => new EnumerableConverter<TEnumerable, TElement>(getEnumerable, elementConverter),
			CollectionConstructionStrategy.Mutable => new MutableEnumerableConverter<TEnumerable, TElement>(getEnumerable, elementConverter, enumerableShape.GetAppender(), enumerableShape.GetDefaultConstructor(), this.GetCollectionOptions(enumerableShape, memberInfluence)),
#if NET
			CollectionConstructionStrategy.Parameterized when !this.owner.DisableHardwareAcceleration && HardwareAccelerated.TryGetConverter<TEnumerable, TElement>(out MessagePackConverter<TEnumerable>? converter) => converter,
#endif
			CollectionConstructionStrategy.Parameterized when getEnumerable is not null && ArraysOfPrimitivesConverters.TryGetConverter(getEnumerable, enumerableShape.GetParameterizedConstructor(), out MessagePackConverter<TEnumerable>? converter) => converter,
			CollectionConstructionStrategy.Parameterized => new SpanEnumerableConverter<TEnumerable, TElement>(getEnumerable, elementConverter, enumerableShape.GetParameterizedConstructor(), this.GetCollectionOptions(enumerableShape, memberInfluence)),
			_ => throw new NotSupportedException($"Unrecognized enumerable pattern: {typeof(TEnumerable).Name}"),
		};
	}

	/// <inheritdoc/>
	public override object? VisitEnum<TEnum, TUnderlying>(IEnumTypeShape<TEnum, TUnderlying> enumShape, object? state = null)
	{
		if (this.GetCustomConverter(enumShape, enumShape.AttributeProvider) is MessagePackConverter<TEnum> customConverter)
		{
			return customConverter;
		}

		return this.owner.SerializeEnumValuesByName
			? new EnumAsStringConverter<TEnum, TUnderlying>(this.GetConverter(enumShape.UnderlyingType), enumShape.Members)
			: new EnumAsOrdinalConverter<TEnum, TUnderlying>(this.GetConverter(enumShape.UnderlyingType));
	}

	/// <inheritdoc/>
	public override object? VisitSurrogate<T, TSurrogate>(ISurrogateTypeShape<T, TSurrogate> surrogateShape, object? state = null)
		=> new SurrogateConverter<T, TSurrogate>(surrogateShape, this.GetConverter(surrogateShape.SurrogateType, state: state));

	/// <summary>
	/// Gets or creates a converter for the given type shape.
	/// </summary>
	/// <typeparam name="T">The data type to make convertible.</typeparam>
	/// <param name="shape">The type shape.</param>
	/// <param name="memberAttributes">The attribute provider on the member that requires this converter.</param>
	/// <param name="state">An optional state object to pass to the converter.</param>
	/// <returns>The converter.</returns>
	protected MessagePackConverter<T> GetConverter<T>(ITypeShape<T> shape, ICustomAttributeProvider? memberAttributes = null, object? state = null)
	{
		if (memberAttributes is not null)
		{
			if (state is not null)
			{
				throw new ArgumentException("Providing both attributes and state are not supported because we reuse the state parameter for attribute influence.");
			}

			if (memberAttributes.GetCustomAttribute<UseComparerAttribute>() is { } attribute)
			{
				MemberConverterInfluence memberInfluence = new()
				{
					ComparerSource = attribute.ComparerType,
					ComparerSourceMemberName = attribute.MemberName,
				};

				// PERF: Ideally, we can store and retrieve member influenced converters
				// just like we do for non-member influenced ones.
				// We'd probably use a separate dictionary dedicated to member-influenced converters.
				return (MessagePackConverter<T>)shape.Invoke(this, memberInfluence)!;
			}
		}

		return (MessagePackConverter<T>)this.context.GetOrAdd(shape, state)!;
	}

	/// <summary>
	/// Gets or creates a converter for the given type shape.
	/// </summary>
	/// <param name="shape">The type shape.</param>
	/// <param name="state">An optional state object to pass to the converter.</param>
	/// <returns>The converter.</returns>
	protected IMessagePackConverterInternal GetConverter(ITypeShape shape, object? state = null)
	{
		ITypeShapeFunc self = this;
		return (IMessagePackConverterInternal)shape.Invoke(this, state)!;
	}

	private static void ThrowIfAlreadyAssigned<TArgumentState>(in TArgumentState argumentState, int position, string name)
		where TArgumentState : IArgumentState
	{
		if (argumentState.IsArgumentSet(position))
		{
			Throw(name);

			[DoesNotReturn]
			static void Throw(string name)
				=> throw new MessagePackSerializationException($"The parameter '{name}' has already been assigned a value.")
				{
					Code = MessagePackSerializationException.ErrorCode.DoublePropertyAssignment,
				};
		}
	}

	/// <summary>
	/// Returns a dictionary of <see cref="MessagePackConverter{T}"/> objects for each subtype, keyed by their alias.
	/// </summary>
	/// <param name="objectShape">The shape of the data type that may define derived types that are also allowed for serialization.</param>
	/// <param name="baseTypeConverter">The converter to use when serializing the base type itself.</param>
	/// <returns>A dictionary of <see cref="MessagePackConverter{T}"/> objects, keyed by the alias by which they will be identified in the data stream.</returns>
	/// <exception cref="InvalidOperationException">Thrown if <paramref name="objectShape"/> has any <see cref="DerivedTypeShapeAttribute"/> that violates rules.</exception>
	private SubTypes<TBaseType>? DiscoverUnionTypes<TBaseType>(IObjectTypeShape<TBaseType> objectShape, MessagePackConverter<TBaseType> baseTypeConverter)
	{
		IReadOnlyDictionary<DerivedTypeIdentifier, ITypeShape>? mapping;
		if (!this.owner.TryGetDynamicSubTypes(objectShape.Type, out mapping))
		{
			return null;
		}

		Dictionary<int, MessagePackConverter> deserializeByIntData = new();
		Dictionary<ReadOnlyMemory<byte>, MessagePackConverter> deserializeByUtf8Data = new();
		Dictionary<Type, (DerivedTypeIdentifier Alias, MessagePackConverter Converter, ITypeShape Shape)> serializerData = new();
		foreach (KeyValuePair<DerivedTypeIdentifier, ITypeShape> pair in mapping)
		{
			DerivedTypeIdentifier alias = pair.Key;
			ITypeShape shape = pair.Value;

			// We don't want a reference-preserving converter here because that layer has already run
			// by the time our subtype converter is invoked.
			// And doubling up on it means values get serialized incorrectly.
			MessagePackConverter converter = shape.Type == objectShape.Type ? baseTypeConverter : this.GetConverter(shape).UnwrapReferencePreservation();
			switch (alias.Type)
			{
				case DerivedTypeIdentifier.AliasType.Integer:
					deserializeByIntData.Add(alias.IntAlias, converter);
					break;
				case DerivedTypeIdentifier.AliasType.String:
					deserializeByUtf8Data.Add(alias.Utf8Alias, converter);
					break;
				default:
					throw new NotImplementedException("Unspecified alias type.");
			}

			Verify.Operation(serializerData.TryAdd(shape.Type, (alias, converter, shape)), $"The type {objectShape.Type.FullName} has more than one subtype with a duplicate alias: {alias}.");
		}

		// Our runtime type checks must be done in an order that will select the most derived matching type.
		(DerivedTypeIdentifier Alias, MessagePackConverter Converter, ITypeShape Shape)[] sortedTypes = serializerData.Values.ToArray();
		Array.Sort(sortedTypes, (a, b) => DerivedTypeComparer.Default.Compare(a.Shape.Type, b.Shape.Type));

		return new SubTypes<TBaseType>
		{
			DeserializersByIntAlias = deserializeByIntData.ToFrozenDictionary(),
			DeserializersByStringAlias = new SpanDictionary<byte, MessagePackConverter>(deserializeByUtf8Data, ByteSpanEqualityComparer.Ordinal),
			Serializers = serializerData.Select(t => t.Value).ToFrozenSet(),
			TryGetSerializer = (ref TBaseType v) =>
			{
				if (v is null)
				{
					return null;
				}

				foreach ((DerivedTypeIdentifier Alias, MessagePackConverter Converter, ITypeShape Shape) pair in sortedTypes)
				{
					if (pair.Shape.Type.IsAssignableFrom(v.GetType()))
					{
						return (pair.Alias, pair.Converter);
					}
				}

				return null;
			},
		};
	}

	private MessagePackConverter<T>? GetCustomConverter<T>(ITypeShape<T> typeShape, ICustomAttributeProvider? attributeProvider)
	{
		if (attributeProvider?.GetCustomAttribute<MessagePackConverterAttribute>() is not { } customConverterAttribute)
		{
			return null;
		}

		Type converterType = customConverterAttribute.ConverterType;
		if ((typeShape.GetAssociatedTypeShape(converterType) as IObjectTypeShape)?.GetDefaultConstructor() is Func<object> converterFactory)
		{
			MessagePackConverter<T> converter = (MessagePackConverter<T>)converterFactory();
			if (this.owner.PreserveReferences != ReferencePreservationMode.Off)
			{
				converter = converter.WrapWithReferencePreservation();
			}

			return converter;
		}

		if (converterType.GetConstructor(Type.EmptyTypes) is not ConstructorInfo ctor)
		{
			throw new MessagePackSerializationException($"{typeof(T).FullName} has {typeof(MessagePackConverterAttribute)} that refers to {customConverterAttribute.ConverterType.FullName} but that converter has no default constructor.");
		}

		return (MessagePackConverter<T>)ctor.Invoke(Array.Empty<object?>());
	}

	private CollectionConstructionOptions<TKey> GetCollectionOptions<TDictionary, TKey, TValue>(IDictionaryTypeShape<TDictionary, TKey, TValue> dictionaryShape, MemberConverterInfluence? memberInfluence)
		where TKey : notnull
		=> this.GetCollectionOptions(dictionaryShape.KeyType, dictionaryShape.SupportedComparer, memberInfluence);

	private CollectionConstructionOptions<TElement> GetCollectionOptions<TEnumerable, TElement>(IEnumerableTypeShape<TEnumerable, TElement> enumerableShape, MemberConverterInfluence? memberInfluence)
		=> this.GetCollectionOptions(enumerableShape.ElementType, enumerableShape.SupportedComparer, memberInfluence);

	private CollectionConstructionOptions<TKey> GetCollectionOptions<TKey>(ITypeShape<TKey> keyShape, CollectionComparerOptions requiredComparer, MemberConverterInfluence? memberInfluence)
	{
		if (this.owner.ComparerProvider is null)
		{
			return default;
		}

		try
		{
			return requiredComparer switch
			{
				CollectionComparerOptions.None => default,
				CollectionComparerOptions.Comparer => new() { Comparer = memberInfluence?.GetComparer<TKey>() ?? this.owner.ComparerProvider.GetComparer(keyShape) },
				CollectionComparerOptions.EqualityComparer => new() { EqualityComparer = memberInfluence?.GetEqualityComparer<TKey>() ?? this.owner.ComparerProvider.GetEqualityComparer(keyShape) },
				_ => throw new NotSupportedException(),
			};
		}
		catch (NotSupportedException ex) when (typeof(TKey) == typeof(object))
		{
			throw new NotSupportedException("Serializing dictionaries or hash sets with System.Object keys is not supported. Consider using a strong-typed key with properties, or using a custom MessagePackSerializer.ComparerProvider.", ex);
		}
	}

	/// <summary>
	/// A comparer that sorts types by their inheritance hierarchy, with the most derived types first.
	/// </summary>
	private class DerivedTypeComparer : IComparer<Type>
	{
		internal static readonly DerivedTypeComparer Default = new();

		private DerivedTypeComparer()
		{
		}

		public int Compare(Type? x, Type? y)
		{
			// This proprietary implementation does not expect null values.
			Requires.NotNull(x!);
			Requires.NotNull(y!);

			return
				x.IsAssignableFrom(y) ? 1 :
				y.IsAssignableFrom(x) ? -1 :
				0;
		}
	}

	/// <summary>
	/// Captures the influence of a member on a converter.
	/// </summary>
	/// <remarks>
	/// This must be hashable/equatable so that we can cache converters based on this influence.
	/// </remarks>
	private record MemberConverterInfluence
	{
		/// <summary>
		/// Gets the type that provides the comparer, if specified by the member.
		/// </summary>
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
		public Type? ComparerSource { get; init; }

		/// <summary>
		/// Gets the name of the property on <see cref="ComparerSource"/> that provides the comparer, if specified by the member.
		/// </summary>
		public string? ComparerSourceMemberName { get; init; }

		/// <summary>
		/// Gets the equality comparer for the specified type, if a comparer source is specified.
		/// </summary>
		/// <typeparam name="T">The type to be compared.</typeparam>
		/// <returns>The equality comparer, if available.</returns>
		public IEqualityComparer<T>? GetEqualityComparer<T>() => this.ComparerSource is null ? null : (IEqualityComparer<T>)this.ActivateComparer();

		/// <summary>
		/// Gets the comparer for the specified type, if a comparer source is specified.
		/// </summary>
		/// <typeparam name="T">The type to be compared.</typeparam>
		/// <returns>The comparer, if available.</returns>
		public IComparer<T>? GetComparer<T>() => this.ComparerSource is null ? null : (IComparer<T>)this.ActivateComparer();

		/// <summary>
		/// Gets the comparer from the specified type and member.
		/// </summary>
		/// <returns>The comparer.</returns>
		/// <exception cref="InvalidOperationException">Thrown if something goes wrong in obtaining the comparer from the given type and member.</exception>
		private object ActivateComparer()
		{
			Verify.Operation(this.ComparerSource is not null, "Comparer source is not specified.");

			MethodInfo? propertyGetter = null;
			if (this.ComparerSourceMemberName is not null)
			{
				PropertyInfo? property = this.ComparerSource.GetProperty(this.ComparerSourceMemberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
				if (property is not { GetMethod: { } getter })
				{
					throw new InvalidOperationException($"Unable to find public property '{this.ComparerSourceMemberName}' on type '{this.ComparerSource.FullName}' with getter.");
				}

				if (getter.IsStatic)
				{
					return getter.Invoke(null, null) ?? throw CreateNullPropertyValueError();
				}

				propertyGetter = getter;
			}

			object? instance = Activator.CreateInstance(this.ComparerSource) ?? throw new InvalidOperationException($"Unable to activate {this.ComparerSource}.");

			return propertyGetter is null ? instance : propertyGetter.Invoke(instance, null) ?? CreateNullPropertyValueError();

			InvalidOperationException CreateNullPropertyValueError() => new InvalidOperationException($"{this.ComparerSource.FullName}.{this.ComparerSourceMemberName} produced a null value.");
		}
	}
}
