// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileFluss.Core.Settings;

/// <summary>
/// Reads enums by name, case-insensitively, and turns an unknown value into the default
/// instead of failing the whole settings file. Lesson from the NetFluss port: one bad enum
/// value (<c>"de"</c> for a language) made the store fall back to defaults and then save
/// those over every setting the user had.
/// </summary>
public sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class LenientEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : default;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
            {
                var value = (T)Enum.ToObject(typeof(T), number);
                return Enum.IsDefined(value) ? value : default;
            }

            reader.Skip();
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
