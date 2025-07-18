﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

[Trait("AsyncSerialization", "true")]
public partial class AsyncSerializationTests : MessagePackSerializerTestBase
{
	[Fact]
	public async Task RoundtripPoco() => await this.AssertRoundtripAsync(new Poco(1, 2));

	[Fact]
	public async Task RoundtripPocoWithDefaultCtor() => await this.AssertRoundtripAsync(new PocoWithDefaultCtor { X = 1, Y = 2 });

	[Fact]
	public async Task LargeArray() => await this.AssertRoundtripAsync(new ArrayOfPocos(Enumerable.Range(0, 1000).Select(i => new Poco(i, i)).ToArray()));

	[Fact]
	public async Task LargeList() => await this.AssertRoundtripAsync(new ListOfPocos(Enumerable.Range(0, 1000).Select(i => new Poco(i, i)).ToList()));

	[Fact]
	public async Task LargeImmutableArray() => await this.AssertRoundtripAsync(new ImmutableArrayOfPocos(Enumerable.Range(0, 1000).Select(i => new Poco(i, i)).ToImmutableArray()));

	[Fact]
	public async Task Null_Array() => await this.AssertRoundtripAsync(new ArrayOfPocos(null));

	[Fact]
	public async Task Null() => await this.AssertRoundtripAsync<Poco>(null);

	[Fact]
	public async Task ArrayOfInts() => await this.AssertRoundtripAsync(new ArrayOfPrimitives([1, 2, 3]));

	[Fact]
	public async Task ObjectAsArrayOfValues() => await this.AssertRoundtripAsync(new PocoAsArray(42));

	[Fact]
	public async Task ObjectAsArrayOfValues_Null() => await this.AssertRoundtripAsync<PocoAsArray>(null);

	[Fact]
	public async Task ObjectAsArrayOfValues_DefaultCtor() => await this.AssertRoundtripAsync(new PocoAsArrayWithDefaultCtor { Value = 42 });

	[Fact]
	public async Task ObjectAsArrayOfValues_DefaultCtor_Null() => await this.AssertRoundtripAsync<PocoAsArrayWithDefaultCtor>(null);

	[Fact]
	public async Task WithPreBuffering()
	{
		SpecialRecordConverter converter = new();
		this.Serializer = this.Serializer with { Converters = [converter] };
		var msgpack = new ReadOnlySequence<byte>(
			this.Serializer.Serialize(new SpecialRecord { Property = 446 }, TestContext.Current.CancellationToken));

		// Verify that with a sufficiently low async buffer, the async paths are taken.
		this.Serializer = new()
		{
			MaxAsyncBuffer = 1,
			Converters = [converter],
		};
		await this.Serializer.DeserializeAsync<SpecialRecord>(new FragmentedPipeReader(msgpack), TestContext.Current.CancellationToken);
		Assert.Equal(1, converter.AsyncDeserializationCounter);

		// Verify that with a sufficiently high async buffer, the sync paths are taken.
		converter.AsyncDeserializationCounter = 0;
		this.Serializer = new()
		{
			MaxAsyncBuffer = 15,
			Converters = [converter],
		};
		await this.Serializer.DeserializeAsync<SpecialRecord>(new FragmentedPipeReader(msgpack), TestContext.Current.CancellationToken);
		Assert.Equal(0, converter.AsyncDeserializationCounter);
	}

	[Fact]
	public async Task DecodeLargeString()
	{
		string expected = new string('a', 100 * 1024);
		ReadOnlySequence<byte> msgpack = new(this.Serializer.Serialize<string, Witness>(expected, TestContext.Current.CancellationToken));
		FragmentedPipeReader pipeReader = new(msgpack, msgpack.GetPosition(0), msgpack.GetPosition(1), msgpack.GetPosition(512), msgpack.GetPosition(6000), msgpack.GetPosition(32 * 1024));
		string? actual = await this.Serializer.DeserializeAsync<string>(pipeReader, Witness.ShapeProvider, TestContext.Current.CancellationToken);
		Assert.Equal(expected, actual);
	}

	[Fact]
	public async Task DecodeEmptyString()
	{
		string expected = string.Empty;
		ReadOnlySequence<byte> msgpack = new(this.Serializer.Serialize<string, Witness>(expected, TestContext.Current.CancellationToken));
		FragmentedPipeReader pipeReader = new(msgpack, msgpack.GetPosition(0));
		string? actual = await this.Serializer.DeserializeAsync<string>(pipeReader, Witness.ShapeProvider, TestContext.Current.CancellationToken);
		Assert.Equal(expected, actual);
	}

	[Theory, PairwiseData]
	public async Task DeserializeAsyncAdvancesPipeReader(bool forceAsync)
	{
		this.Serializer = this.Serializer with { MaxAsyncBuffer = forceAsync ? 0 : 1024 };
		using Sequence<byte> sequence = new();
		MessagePackWriter writer = new(sequence);
		writer.Write(42);
		writer.Flush();
		sequence.Write("a"u8);

		PipeReader reader = PipeReader.Create(sequence);

		// Deserialize a value. It should advance the reader exactly across the msgpack structure.
		int number = await this.Serializer.DeserializeAsync<int>(reader, Witness.ShapeProvider, TestContext.Current.CancellationToken);
		Assert.Equal(42, number);

		// Verify that the reader is now positioned at the next byte.
		ReadResult readResult = await reader.ReadAsync(TestContext.Current.CancellationToken);
		Assert.True(readResult.IsCompleted);
		Assert.Equal("a"u8, readResult.Buffer.ToArray());
	}

	[GenerateShapeFor<string>]
	[GenerateShapeFor<int>]
	private partial class Witness;

	[GenerateShape]
	public partial record Poco(int X, int Y);

	[GenerateShape]
	public partial record PocoWithDefaultCtor
	{
		public int X { get; set; }

		public int Y { get; set; }
	}

	[GenerateShape]
	public partial class ArrayOfPocos(Poco[]? pocos) : IEquatable<ArrayOfPocos>
	{
		public Poco[]? Pocos => pocos;

		public bool Equals(ArrayOfPocos? other) => other is not null && StructuralEquality.Equal(this.Pocos, other.Pocos);
	}

	[GenerateShape]
	public partial class ListOfPocos(List<Poco>? pocos) : IEquatable<ListOfPocos>
	{
		public List<Poco>? Pocos => pocos;

		public bool Equals(ListOfPocos? other) => other is not null && StructuralEquality.Equal(this.Pocos, other.Pocos);
	}

	[GenerateShape]
	public partial class ImmutableArrayOfPocos(ImmutableArray<Poco>? pocos) : IEquatable<ImmutableArrayOfPocos>
	{
		public ImmutableArray<Poco>? Pocos => pocos;

		public bool Equals(ImmutableArrayOfPocos? other) => other is not null && StructuralEquality.Equal(this.Pocos, other.Pocos);
	}

	[GenerateShape]
	public partial class ArrayOfPrimitives(int[]? values) : IEquatable<ArrayOfPrimitives>
	{
		public int[]? Values => values;

		public bool Equals(ArrayOfPrimitives? other) => other is not null && StructuralEquality.Equal(this.Values, other.Values);
	}

	[GenerateShape]
	public partial record PocoAsArray([property: Key(0)] int Value);

	[GenerateShape]
	public partial record PocoAsArrayWithDefaultCtor
	{
		[Key(0)]
		public int Value { get; set; }
	}

	[GenerateShape]
	internal partial record SpecialRecord
	{
		internal int Property { get; set; }
	}

	internal class SpecialRecordConverter : MessagePackConverter<SpecialRecord>
	{
		public override bool PreferAsyncSerialization => true;

		internal int AsyncDeserializationCounter { get; set; }

		public override SpecialRecord? Read(ref MessagePackReader reader, SerializationContext context)
		{
			return new SpecialRecord { Property = reader.ReadInt32() };
		}

		public override void Write(ref MessagePackWriter writer, in SpecialRecord? value, SerializationContext context)
		{
			writer.Write(value!.Property);
		}

#pragma warning disable NBMsgPackAsync // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
		public override ValueTask<SpecialRecord?> ReadAsync(MessagePackAsyncReader reader, SerializationContext context)
		{
			this.AsyncDeserializationCounter++;
			return base.ReadAsync(reader, context);
		}
#pragma warning restore NBMsgPackAsync // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
	}
}
