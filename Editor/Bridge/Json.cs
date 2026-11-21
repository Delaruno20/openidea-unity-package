using System.Globalization;
using System.Text;

namespace OpenIDEA.Unity
{
    /// Just enough JSON writing for the bridge's replies (no dependency on a JSON package).
    internal sealed class Json
    {
        readonly StringBuilder sb = new StringBuilder();
        bool needsComma;

        public Json Begin() { Separator(); sb.Append('{'); needsComma = false; return this; }
        public Json End() { sb.Append('}'); needsComma = true; return this; }
        public Json BeginArray(string name) { Key(name); sb.Append('['); needsComma = false; return this; }
        public Json EndArray() { sb.Append(']'); needsComma = true; return this; }

        public Json Field(string name, string value)
        {
            Key(name);
            if (value == null) sb.Append("null");
            else Quote(sb, value);
            needsComma = true;
            return this;
        }

        public Json Field(string name, long value) { Key(name); sb.Append(value.ToString(CultureInfo.InvariantCulture)); needsComma = true; return this; }
        public Json Field(string name, double value) { Key(name); sb.Append(value.ToString("R", CultureInfo.InvariantCulture)); needsComma = true; return this; }
        public Json Field(string name, bool value) { Key(name); sb.Append(value ? "true" : "false"); needsComma = true; return this; }

        public override string ToString() => sb.ToString();

        void Key(string name)
        {
            Separator();
            Quote(sb, name);
            sb.Append(':');
            needsComma = false;
        }

        void Separator()
        {
            if (needsComma) sb.Append(',');
        }

        public static string Escape(string value)
        {
            var b = new StringBuilder(value.Length + 2);
            Quote(b, value);
            return b.ToString();
        }

        static void Quote(StringBuilder b, string value)
        {
            b.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            b.Append('"');
        }
    }
}
