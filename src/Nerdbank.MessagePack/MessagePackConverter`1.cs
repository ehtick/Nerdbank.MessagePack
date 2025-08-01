﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft;

namespace Nerdbank.MessagePack;

/// <summary>
/// An base class for all MessagePack converters.
/// </summary>
/// <typeparam name="T">The data type that can be converted by this object.</typeparam>
/// <remarks>
/// <para>
/// Authors of derived types should review <see href="https://aarnott.github.io/Nerdbank.MessagePack/docs/custom-converters.html">this documentation</see>
/// for important guidance on implementing a converter.
/// </para>
/// <para>
/// Key points to remember about each <see cref="Write"/> or <see cref="Read"/> method (or their async equivalents):
/// <list type="bullet">
/// <item>Read or write exactly one msgpack structure. Use an array or map header for multiple values.</item>
/// <item>Call <see cref="SerializationContext.DepthStep"/> before any significant work.</item>
/// <item>Delegate serialization of sub-values to a converter obtained using <see cref="SerializationContext.GetConverter{T}(ITypeShapeProvider)"/> rather than making a top-level call back to <see cref="MessagePackSerializer"/>.</item>
/// </list>
/// </para>
/// <para>
/// Implementations are encouraged to override <see cref="GetJsonSchema(Nerdbank.MessagePack.JsonSchemaContext, ITypeShape)"/> in order to support
/// <see cref="MessagePackSerializer.GetJsonSchema(ITypeShape)"/>.
/// </para>
/// </remarks>
public abstract class MessagePackConverter<T> : MessagePackConverter, IMessagePackConverterInternal
{
	/// <inheritdoc />
	public override bool PreferAsyncSerialization => false;

	/// <inheritdoc />
	internal override Type DataType => typeof(T);

	/// <summary>
	/// Serializes an instance of <typeparamref name="T"/>.
	/// </summary>
	/// <param name="writer">The writer to use.</param>
	/// <param name="value">The value to serialize.</param>
	/// <param name="context">Context for the serialization.</param>
	/// <remarks>
	/// Implementations of this method should not flush the writer.
	/// </remarks>
	public abstract void Write(ref MessagePackWriter writer, in T? value, SerializationContext context);

	/// <summary>
	/// Deserializes an instance of <typeparamref name="T"/>.
	/// </summary>
	/// <param name="reader">The reader to use.</param>
	/// <param name="context">Context for the deserialization.</param>
	/// <returns>The deserialized value.</returns>
	public abstract T? Read(ref MessagePackReader reader, SerializationContext context);

	/// <summary>
	/// Serializes an instance of <typeparamref name="T"/>.
	/// </summary>
	/// <param name="writer">The writer to use.</param>
	/// <param name="value">The value to serialize.</param>
	/// <param name="context">Context for the serialization.</param>
	/// <returns>A task that tracks the async serialization.</returns>
	/// <remarks>
	/// <para>
	/// The default implementation delegates to <see cref="Write"/> and then flushes the data to the pipe
	/// if the buffers are getting relatively full.
	/// </para>
	/// <para>
	/// Derived classes should only override this method if they may write a lot of data.
	/// They should do so with the intent of writing fragments of data at a time and periodically call
	/// <see cref="MessagePackAsyncWriter.FlushIfAppropriateAsync"/>
	/// in order to keep the size of memory buffers from growing too much.
	/// </para>
	/// </remarks>
	public virtual ValueTask WriteAsync(MessagePackAsyncWriter writer, T? value, SerializationContext context)
	{
		Requires.NotNull(writer);
		context.CancellationToken.ThrowIfCancellationRequested();

		MessagePackWriter syncWriter = writer.CreateWriter();
		this.Write(ref syncWriter, value, context);
		writer.ReturnWriter(ref syncWriter);

		// On our way out, pause to flush the pipe if a lot of data has accumulated in the buffer.
		return writer.FlushIfAppropriateAsync(context);
	}

	/// <summary>
	/// Deserializes an instance of <typeparamref name="T"/>.
	/// </summary>
	/// <param name="reader">The reader to use.</param>
	/// <param name="context">Context for the deserialization.</param>
	/// <returns>The deserialized value.</returns>
	/// <remarks>
	/// <para>The default implementation delegates to <see cref="Read"/> after ensuring there is sufficient buffer to read the next structure.</para>
	/// <para>
	/// Derived classes should only override this method if they may read a lot of data.
	/// They should do so with the intent to be able to read some data then asynchronously wait for data before reading more
	/// in order to reduce the amount of memory required to buffer.
	/// </para>
	/// </remarks>
	public virtual async ValueTask<T?> ReadAsync(MessagePackAsyncReader reader, SerializationContext context)
	{
		Requires.NotNull(reader);
		context.CancellationToken.ThrowIfCancellationRequested();

		await reader.BufferNextStructureAsync(context).ConfigureAwait(false);
		MessagePackReader syncReader = reader.CreateBufferedReader();
		T? result = this.Read(ref syncReader, context);
		reader.ReturnReader(ref syncReader);
		return result;
	}

	/// <summary>
	/// Gets the <see href="https://json-schema.org/">JSON schema</see> that resembles the data structure that this converter can serialize and deserialize.
	/// </summary>
	/// <param name="context">A means to obtain schema fragments for inclusion when your converter delegates to other converters.</param>
	/// <param name="typeShape">The shape of the type <typeparamref name="T"/>, in case it provides useful metadata for constructing the schema.</param>
	/// <returns>The fragment of JSON schema that describes the value written by this converter, or <see langword="null" /> if this method has not been overridden.</returns>
	/// <remarks>
	/// <para>
	/// Implementations should return a new instance of <see cref="JsonObject"/> that represents the JSON schema fragment for every caller.
	/// A shared instance <em>may</em> be used to call <see cref="JsonNode.DeepClone"/> and the result returned.
	/// </para>
	/// <para>
	/// The <c>type</c> property should <em>not</em> include "null" on account of the converted type being a reference type.
	/// It is the <em>consumer</em> of the object that will determine whether <see langword="null" /> is an acceptable value of the object.
	/// </para>
	/// <para>
	/// Custom converters that do <em>not</em> override this method will lead to a JSON schema that does not describe the written data, and allows any data as input.
	/// </para>
	/// <para>
	/// If the converter delegates to other converters, the schemas for those sub-values can be obtained for inclusion in the returned schema
	/// by calling <see cref="JsonSchemaContext.GetJsonSchema(ITypeShape)"/> on the <paramref name="context"/>.
	/// </para>
	/// </remarks>
	/// <seealso cref="CreateMsgPackExtensionSchema"/>
	/// <seealso cref="CreateJsonValue(object?)"/>
	/// <seealso cref="ApplyJsonSchemaNullability(JsonObject)"/>
	public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;

	/// <inheritdoc/>
	public override ValueTask<bool> SkipToPropertyValueAsync(MessagePackAsyncReader reader, IPropertyShape propertyShape, SerializationContext context)
		=> throw new NotSupportedException($"The {this.GetType().FullName} converter does not support this operation.");

	/// <inheritdoc/>
	public override ValueTask<bool> SkipToIndexValueAsync(MessagePackAsyncReader reader, object? index, SerializationContext context)
		=> throw new NotSupportedException($"The {this.GetType().FullName} converter does not support this operation.");

	/// <inheritdoc/>
	public override sealed void WriteObject(ref MessagePackWriter writer, object? value, SerializationContext context) => this.Write(ref writer, (T?)value, context);

	/// <inheritdoc/>
	public override sealed object? ReadObject(ref MessagePackReader reader, SerializationContext context) => this.Read(ref reader, context);

	/// <inheritdoc/>
	[EditorBrowsable(EditorBrowsableState.Never)] // Use the generic methods instead.
	public override sealed ValueTask WriteObjectAsync(MessagePackAsyncWriter writer, object? value, SerializationContext context) => this.WriteAsync(writer, (T?)value, context);

	/// <inheritdoc/>
	[EditorBrowsable(EditorBrowsableState.Never)] // Use the generic methods instead.
	public override sealed async ValueTask<object?> ReadObjectAsync(MessagePackAsyncReader reader, SerializationContext context) => await this.ReadAsync(reader, context).ConfigureAwait(false);

	/// <inheritdoc/>
	MessagePackConverter IMessagePackConverterInternal.WrapWithReferencePreservation() => this.WrapWithReferencePreservation();

	/// <inheritdoc/>
	MessagePackConverter IMessagePackConverterInternal.UnwrapReferencePreservation() => this.UnwrapReferencePreservation();

	/// <inheritdoc cref="IMessagePackConverterInternal.WrapWithReferencePreservation" />
	internal virtual MessagePackConverter<T> WrapWithReferencePreservation() => typeof(T).IsValueType ? this : new ReferencePreservingConverter<T>(this);

	/// <inheritdoc cref="IMessagePackConverterInternal.UnwrapReferencePreservation" />
	internal virtual MessagePackConverter<T> UnwrapReferencePreservation() => this;

	/// <inheritdoc/>
	internal override sealed void DerivationGuard()
	{
		throw new NotImplementedException();
	}

	/// <summary>
	/// Transforms a JSON schema to include "null" as a possible value for the schema.
	/// </summary>
	/// <param name="schema">The schema to transform. This value may be mutated.</param>
	/// <returns>The result of the transformation, which may be a different root object than given in <paramref name="schema"/>.</returns>
	/// <remarks>
	/// This is provided as a helper function for <see cref="GetJsonSchema(JsonSchemaContext, ITypeShape)"/> implementations.
	/// </remarks>
	protected internal static JsonObject ApplyJsonSchemaNullability(JsonObject schema)
	{
		Requires.NotNull(schema);

		if (schema.TryGetPropertyValue("type", out JsonNode? typeValue))
		{
			if (schema["type"] is JsonArray types)
			{
				if (!types.Any(n => n?.GetValueKind() == System.Text.Json.JsonValueKind.String && n.GetValue<string>() == "null"))
				{
					types.Add((JsonNode)"null");
				}
			}
			else
			{
				schema["type"] = new JsonArray { (JsonNode)(string)typeValue!, (JsonNode)"null" };
			}
		}
		else
		{
			// This is probably a schema reference.
			schema = new()
			{
				["oneOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }),
			};
		}

		return schema;
	}

	/// <summary>
	/// Creates a JSON schema fragment that describes a type that has no documented schema.
	/// </summary>
	/// <param name="undocumentingConverter">The converter that has not provided a schema.</param>
	/// <returns>The JSON schema fragment that permits anything and explains why.</returns>
	/// <remarks>
	/// This is provided as a helper function for <see cref="GetJsonSchema(JsonSchemaContext, ITypeShape)"/> implementations.
	/// </remarks>
	protected internal static JsonObject CreateUndocumentedSchema(Type undocumentingConverter)
	{
		Requires.NotNull(undocumentingConverter);

		return new()
		{
			["type"] = new JsonArray("number", "integer", "string", "boolean", "object", "array", "null"),
			["description"] = $"The schema of this object is unknown as it is determined by the {undocumentingConverter.FullName} converter which does not override {nameof(MessagePackConverter<int>.GetJsonSchema)}.",
		};
	}

	/// <summary>
	/// Creates a JSON schema fragment that provides a cursory description of a MessagePack extension.
	/// </summary>
	/// <param name="extensionCode">The extension code used.</param>
	/// <returns>A JSON schema fragment.</returns>
	/// <remarks>
	/// This is provided as a helper function for <see cref="GetJsonSchema(JsonSchemaContext, ITypeShape)"/> implementations.
	/// </remarks>
	protected static JsonObject CreateMsgPackExtensionSchema(sbyte extensionCode) => new()
	{
		["type"] = "string",
		["pattern"] = FormattableString.Invariant($"^msgpack extension {extensionCode} as base64: "),
	};

	/// <summary>
	/// Creates a JSON schema fragment that provides a cursory description of a MessagePack binary blob.
	/// </summary>
	/// <param name="description">An optional description to include with the schema.</param>
	/// <returns>A JSON schema fragment.</returns>
	/// <remarks>
	/// This is provided as a helper function for <see cref="GetJsonSchema(JsonSchemaContext, ITypeShape)"/> implementations.
	/// </remarks>
	protected static JsonObject CreateMsgPackBinarySchema(string? description = null)
	{
		JsonObject schema = new()
		{
			["type"] = "string",
			["pattern"] = "^msgpack binary as base64: ",
		};

		if (description is not null)
		{
			schema["description"] = description;
		}

		return schema;
	}

	/// <summary>
	/// Wraps a boxed primitive as a <see cref="JsonValue"/>.
	/// </summary>
	/// <param name="value">The boxed primitive to wrap as a <see cref="JsonValue"/>. Only certain primitives are supported (roughly those supported by non-generic overloads of <c>JsonValue.Create</c>.</param>
	/// <returns>The <see cref="JsonValue"/>, or <see langword="null" /> if <paramref name="value"/> is <see langword="null" /> because <see cref="JsonValue"/> does not represent null.</returns>
	/// <exception cref="NotSupportedException">Thrown if <paramref name="value"/> is of a type that cannot be wrapped as a simple JSON value.</exception>
	/// <remarks>
	/// This is provided as a helper function for <see cref="GetJsonSchema(JsonSchemaContext, ITypeShape)"/> implementations.
	/// </remarks>
	[return: NotNullIfNotNull(nameof(value))]
	protected static JsonValue? CreateJsonValue(object? value)
	{
		return value switch
		{
			null => null,
			string v => JsonValue.Create(v),
			short v => JsonValue.Create(v),
			int v => JsonValue.Create(v),
			long v => JsonValue.Create(v),
			float v => JsonValue.Create(v),
			double v => JsonValue.Create(v),
			decimal v => JsonValue.Create(v),
			bool v => JsonValue.Create(v),
			byte v => JsonValue.Create(v),
			sbyte v => JsonValue.Create(v),
			ushort v => JsonValue.Create(v),
			uint v => JsonValue.Create(v),
			ulong v => JsonValue.Create(v),
			char v => JsonValue.Create(v),
			_ => throw new NotSupportedException($"Unsupported object type: {value.GetType().FullName}"),
		};
	}
}
