﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Nodes;
using PolyType.Utilities;

namespace Nerdbank.MessagePack.Converters;

/// <summary>
/// A base class for converters that handle object types.
/// </summary>
/// <typeparam name="T">The type of object to be serialized.</typeparam>
internal abstract class ObjectConverterBase<T> : MessagePackConverter<T>
{
	/// <summary>
	/// Adds a <c>description</c> property to the schema based on the <see cref="DescriptionAttribute"/> that is applied to the target.
	/// </summary>
	/// <param name="attributeProvider">The attribute provider for the target.</param>
	/// <param name="schema">The schema for the target.</param>
	/// <param name="namePrefix">An optional prefix to include in the description, or to use by itself when no <see cref="DescriptionAttribute"/> is present.</param>
	protected static void ApplyDescription(ICustomAttributeProvider? attributeProvider, JsonObject schema, string? namePrefix = null)
	{
		string? description;
		if (attributeProvider?.GetCustomAttribute<DescriptionAttribute>() is DescriptionAttribute descriptionAttribute)
		{
			description = descriptionAttribute.Description;
			if (namePrefix is not null)
			{
				description = $"{namePrefix}: {description}";
			}
		}
		else
		{
			description = namePrefix;
		}

		if (description is not null)
		{
			schema["description"] = description;
		}
	}

	/// <summary>
	/// Adds a <c>default</c> property to the schema based on the <see cref="DefaultValueAttribute"/> that is applied to the property
	/// or the default parameter value assigned to the property's associated constructor parameter.
	/// </summary>
	/// <param name="attributeProvider">The attribute provider for the target.</param>
	/// <param name="propertySchema">The schema for the target.</param>
	/// <param name="parameterShape">The constructor parameter that matches the property, if applicable.</param>
	protected static void ApplyDefaultValue(ICustomAttributeProvider? attributeProvider, JsonObject propertySchema, IParameterShape? parameterShape)
	{
		JsonValue? defaultValue =
			parameterShape?.HasDefaultValue is true ? CreateJsonValue(parameterShape.DefaultValue) :
			attributeProvider?.GetCustomAttribute<DefaultValueAttribute>() is DefaultValueAttribute att ? CreateJsonValue(att.Value) :
			null;

		if (defaultValue is not null)
		{
			propertySchema["default"] = defaultValue;
		}
	}

	/// <summary>
	/// Tests whether a given property is non-nullable.
	/// </summary>
	/// <param name="property">The property.</param>
	/// <param name="associatedParameter">The associated constructor parameter, if any.</param>
	/// <returns>A boolean value.</returns>
	protected static bool IsNonNullable(IPropertyShape property, IParameterShape? associatedParameter)
		=> (!property.HasGetter || property.IsGetterNonNullable) &&
			(!property.HasSetter || property.IsSetterNonNullable) &&
			(associatedParameter is null || associatedParameter.IsNonNullable);

	/// <summary>
	/// Creates a dictionary that maps property names to constructor parameters.
	/// </summary>
	/// <param name="objectShape">The object shape.</param>
	/// <returns>The dictionary.</returns>
	protected static Dictionary<string, IParameterShape>? CreatePropertyAndParameterDictionary(IObjectTypeShape objectShape)
	{
		Dictionary<string, IParameterShape>? ctorParams = objectShape.Constructor?.Parameters
			.Where(p => p.Kind is ParameterKind.MethodParameter || p.IsRequired)
			.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
		return ctorParams;
	}

	/// <summary>
	/// Throws a <see cref="MessagePackSerializationException"/> if required properties are missing during deserialization.
	/// </summary>
	/// <typeparam name="TArgumentState">The argument state type.</typeparam>
	/// <param name="argumentState">The argument state.</param>
	/// <param name="parameters">The parameter shapes.</param>
	/// <param name="defaultValuesPolicy">The policy applied to this deserialization.</param>
	protected static void ThrowIfMissingRequiredProperties<TArgumentState>(in TArgumentState argumentState, IReadOnlyList<IParameterShape> parameters, DeserializeDefaultValuesPolicy defaultValuesPolicy)
		where TArgumentState : IArgumentState
	{
		if ((defaultValuesPolicy & DeserializeDefaultValuesPolicy.AllowMissingValuesForRequiredProperties) == DeserializeDefaultValuesPolicy.AllowMissingValuesForRequiredProperties)
		{
			// The policy is to not enforce required properties.
			return;
		}

		if (argumentState.AreRequiredArgumentsSet)
		{
			// No missing required properties.
			return;
		}

		List<string> missingRequiredParams = [];
		foreach (IParameterShape parameter in parameters)
		{
			if (parameter.IsRequired && !argumentState.IsArgumentSet(parameter.Position))
			{
				missingRequiredParams.Add(parameter.Name);
			}
		}

		Throw($"Missing required properties: {string.Join(", ", missingRequiredParams)}");

		[DoesNotReturn]
		static void Throw(string message)
			=> throw new MessagePackSerializationException(message)
			{
				Code = MessagePackSerializationException.ErrorCode.MissingRequiredProperty,
			};
	}
}
