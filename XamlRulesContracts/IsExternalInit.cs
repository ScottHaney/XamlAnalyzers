// Polyfill so C# records / init-only setters compile on netstandard2.0, which doesn't ship this
// type. The compiler only needs the type to exist; it is never referenced at runtime.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
