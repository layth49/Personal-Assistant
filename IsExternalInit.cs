// Polyfill required to use C# 9 records / init-only setters on .NET Framework.
// The compiler emits references to System.Runtime.CompilerServices.IsExternalInit
// for init accessors, but that type only ships in .NET 5+. Declaring it here lets
// records compile against net48.1. Safe to remove if the project ever moves to a
// target framework that provides this type natively.
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
