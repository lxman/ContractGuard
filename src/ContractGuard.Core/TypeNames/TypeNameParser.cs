namespace ContractGuard.TypeNames;

/// <summary>
/// Recursive-descent parser for type reference names. Grammar:
///   type    := core suffix*
///   core    := tuple | named
///   tuple   := '(' element (',' element)+ ')'
///   element := type identifier?
///   named   := segment ('.' segment)* ('&lt;' type (',' type)* '&gt;')?
///   suffix  := '?' | '*' | '[' ','* ']'
/// </summary>
public static class TypeNameParser
{
    public static TypeNameNode Parse(string text)
    {
        var cursor = new Cursor(text);
        var node = ParseType(ref cursor);
        cursor.SkipWhitespace();
        if (!cursor.AtEnd)
            throw new FormatException($"Unexpected '{cursor.Current}' at position {cursor.Position} in type name '{text}'.");

        return node;
    }

    private static TypeNameNode ParseType(ref Cursor cursor)
    {
        cursor.SkipWhitespace();
        var node = cursor.Current == '(' ? ParseTuple(ref cursor) : ParseNamed(ref cursor);

        while (true)
        {
            cursor.SkipWhitespace();
            if (cursor.TryConsume('?'))
            {
                node = new TypeNameNode.Nullable(node);
            }
            else if (cursor.TryConsume('*'))
            {
                node = new TypeNameNode.Pointer(node);
            }
            else if (cursor.TryConsume('['))
            {
                var rank = 1;
                while (cursor.TryConsume(','))
                    rank++;
                cursor.Expect(']');
                node = new TypeNameNode.Array(node, rank);
            }
            else
            {
                return node;
            }
        }
    }

    private static TypeNameNode ParseTuple(ref Cursor cursor)
    {
        cursor.Expect('(');
        var elements = new List<(string? Name, TypeNameNode Type)>();
        while (true)
        {
            var type = ParseType(ref cursor);
            cursor.SkipWhitespace();
            string? name = null;
            if (cursor.AtIdentifierStart)
                name = cursor.ReadIdentifier();

            elements.Add((name, type));
            cursor.SkipWhitespace();
            if (cursor.TryConsume(','))
                continue;
            cursor.Expect(')');
            break;
        }

        if (elements.Count < 2)
            throw new FormatException("A tuple type needs at least two elements.");

        return new TypeNameNode.Tuple(elements);
    }

    private static TypeNameNode ParseNamed(ref Cursor cursor)
    {
        var name = cursor.ReadIdentifier();
        while (true)
        {
            cursor.SkipWhitespace();
            if (cursor.TryConsume('.'))
            {
                name += "." + cursor.ReadIdentifier();
            }
            else if (cursor.TryConsume('+'))
            {
                name += "+" + cursor.ReadIdentifier();
            }
            else
            {
                break;
            }
        }

        if (!cursor.TryConsume('<'))
            return new TypeNameNode.Named(name, []);

        var args = new List<TypeNameNode>();
        while (true)
        {
            args.Add(ParseType(ref cursor));
            cursor.SkipWhitespace();
            if (cursor.TryConsume(','))
                continue;
            cursor.Expect('>');
            break;
        }

        cursor.SkipWhitespace();
        if (cursor.Current == '.')
            throw new FormatException("Members of constructed generic types (A<B>.C) are not supported in contracts.");

        return new TypeNameNode.Named(name, args);
    }

    private struct Cursor(string text)
    {
        private readonly string _text = text;

        public int Position { get; private set; }

        public readonly bool AtEnd => Position >= _text.Length;

        public readonly char Current => AtEnd ? '\0' : _text[Position];

        public readonly bool AtIdentifierStart => !AtEnd && (char.IsLetter(Current) || Current is '_' or '@');

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(Current))
                Position++;
        }

        public bool TryConsume(char c)
        {
            SkipWhitespace();
            if (Current != c)
                return false;
            Position++;
            return true;
        }

        public void Expect(char c)
        {
            if (!TryConsume(c))
                throw new FormatException($"Expected '{c}' at position {Position} in type name '{_text}'.");
        }

        public string ReadIdentifier()
        {
            SkipWhitespace();
            if (!AtIdentifierStart)
                throw new FormatException($"Expected an identifier at position {Position} in type name '{_text}'.");

            var start = Position;
            if (Current == '@')
                Position++;
            while (!AtEnd && (char.IsLetterOrDigit(Current) || Current == '_'))
                Position++;

            return _text[start..Position];
        }
    }
}
