using System;

namespace KnotGarden.Core.Exceptions;

public sealed class OpenApiParseException : Exception
{
    public OpenApiParseException(string message) : base(message) { }
    public OpenApiParseException(string message, Exception inner) : base(message, inner) { }
}
