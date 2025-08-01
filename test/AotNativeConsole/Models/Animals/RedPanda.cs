﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Originally taken from https://github.com/morfah/MessagePackCompareTest
// Copyright (c) 2025 morfah
namespace AotNativeConsole.Models.Animals;

public class RedPanda : Animal
{
	public RedPanda(Guid id, Vector2 position, float depth)
		: base(id, position, depth, AnimalTypes.RedPanda)
	{
	}
}
