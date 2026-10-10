using System;

namespace ManagedReferencePicker
{
    /// <summary>
    /// Placed on a concrete class to hide it from the type-picker dropdown drawn by
    /// <see cref="ManagedReferencePickerAttribute"/> (attribute sets, effect components, …). Useful for
    /// test fixtures or intermediate types you never want authored on an asset.
    /// <para><c>Inherited = false</c>: only the annotated class is hidden; its subclasses still appear.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class HideInManagedReferencePickerAttribute : Attribute
    {
    }
}
