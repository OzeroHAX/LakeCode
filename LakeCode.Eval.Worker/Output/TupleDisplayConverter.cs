using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LakeCode.Eval.Worker.Output;

internal sealed class TupleDisplayConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(ITuple).IsAssignableFrom(typeToConvert);
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(TupleConverter<>).MakeGenericType(typeToConvert))!;
    private sealed class TupleConverter<T> : JsonConverter<T>
    {
        public TupleConverter() { }
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Display converter is write-only.");
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var tuple = (ITuple)value!;
            writer.WriteStartArray();
            for (int i = 0; i < tuple.Length; i++) JsonSerializer.Serialize(writer, tuple[i], options);
            writer.WriteEndArray();
        }
    }
}
