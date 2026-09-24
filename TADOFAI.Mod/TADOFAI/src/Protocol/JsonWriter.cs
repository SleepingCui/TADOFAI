using System.Globalization;
using System.Text;

namespace TADOFAI.Mod.Protocol
{
    /// <summary>
    /// 极简 JSON 写入器。只做序列化，不依赖任何第三方库（net472 没有内置 System.Text.Json）。
    /// 规则：NaN / Infinity 一律写成 null，字符串超长截断，全部数值使用 InvariantCulture。
    /// </summary>
    public sealed class JsonWriter
    {
        private const int MaxStringLength = 512;

        private readonly StringBuilder _builder = new StringBuilder(256);
        private bool _needsComma;

        public JsonWriter BeginObject()
        {
            Separate();
            _builder.Append('{');
            _needsComma = false;
            return this;
        }

        public JsonWriter EndObject()
        {
            _builder.Append('}');
            _needsComma = true;
            return this;
        }

        public JsonWriter BeginArray()
        {
            Separate();
            _builder.Append('[');
            _needsComma = false;
            return this;
        }

        public JsonWriter EndArray()
        {
            _builder.Append(']');
            _needsComma = true;
            return this;
        }

        public JsonWriter Name(string name)
        {
            Separate();
            WriteEscaped(name);
            _builder.Append(':');
            _needsComma = false;
            return this;
        }

        public JsonWriter Value(string value)
        {
            Separate();
            if (value == null) _builder.Append("null");
            else WriteEscaped(value);
            _needsComma = true;
            return this;
        }

        public JsonWriter Value(bool value)
        {
            Separate();
            _builder.Append(value ? "true" : "false");
            _needsComma = true;
            return this;
        }

        public JsonWriter Value(int value)
        {
            Separate();
            _builder.Append(value.ToString(CultureInfo.InvariantCulture));
            _needsComma = true;
            return this;
        }

        public JsonWriter Value(long value)
        {
            Separate();
            _builder.Append(value.ToString(CultureInfo.InvariantCulture));
            _needsComma = true;
            return this;
        }

        public JsonWriter Value(float value)
        {
            return Value((double)value);
        }

        public JsonWriter Value(double value)
        {
            Separate();
            if (!NumUtil.IsFinite(value))
            {
                // 协议禁止 NaN / Infinity
                _builder.Append("null");
            }
            else
            {
                _builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }
            _needsComma = true;
            return this;
        }

        public JsonWriter Null()
        {
            Separate();
            _builder.Append("null");
            _needsComma = true;
            return this;
        }

        public override string ToString()
        {
            return _builder.ToString();
        }

        private void Separate()
        {
            if (_needsComma) _builder.Append(',');
        }

        private void WriteEscaped(string value)
        {
            if (value == null)
            {
                _builder.Append("null");
                return;
            }

            int length = value.Length;
            if (length > MaxStringLength) length = MaxStringLength;

            _builder.Append('"');
            for (int i = 0; i < length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': _builder.Append("\\\""); break;
                    case '\\': _builder.Append("\\\\"); break;
                    case '\b': _builder.Append("\\b"); break;
                    case '\f': _builder.Append("\\f"); break;
                    case '\n': _builder.Append("\\n"); break;
                    case '\r': _builder.Append("\\r"); break;
                    case '\t': _builder.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            _builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            _builder.Append(c);
                        break;
                }
            }
            _builder.Append('"');
        }
    }
}
