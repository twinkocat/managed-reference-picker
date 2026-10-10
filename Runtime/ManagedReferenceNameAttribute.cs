using System;

namespace ManagedReferencePicker
{
    /// <summary>
    /// Optional friendly name shown for a concrete managed-reference type in the inspector's
    /// type-picker (see <see cref="ManagedReferencePickerAttribute"/>). Without it the picker humanizes
    /// the type name (dropping a trailing "Component").
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class ManagedReferenceNameAttribute : Attribute
    {
        public string DisplayName { get; }

        public ManagedReferenceNameAttribute(string displayName)
        {
            DisplayName = displayName;
        }
    }
}
