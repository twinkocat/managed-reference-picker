using System;
using UnityEngine;

namespace ManagedReferencePicker
{
    /// <summary>
    /// Draws a type dropdown in the inspector that lets you pick a concrete subclass and
    /// creates an instance of it for the decorated field.
    ///
    /// <para>Apply it to a <b>polymorphic</b> field that also carries <see cref="SerializeReference"/>.
    /// It works on a single reference, an array, or a <c>List&lt;T&gt;</c> (Unity applies the drawer to
    /// each element, so every element gets its own type selector):</para>
    ///
    /// <code>
    /// public interface IAbilityBlock { }
    ///
    /// [Serializable]
    /// public class DamageOverTimeBlock : IAbilityBlock { public float damagePerSecond; }
    ///
    /// public class Ability : MonoBehaviour
    /// {
    ///     [SerializeReference, ManagedReferencePicker]
    ///     private List&lt;IAbilityBlock&gt; blocks = new();
    /// }
    /// </code>
    ///
    /// <para>This removes the need for a dedicated builder <see cref="MonoBehaviour"/>/
    /// <see cref="ScriptableObject"/> just to create polymorphic instances — the same job is done
    /// by a single attribute on the field. Concrete types offered by the dropdown must be non-abstract
    /// classes (not <see cref="UnityEngine.Object"/>s) with a parameterless constructor.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class ManagedReferencePickerAttribute : PropertyAttribute
    {
        /// <summary>
        /// When <c>false</c>, any concrete type already present on a sibling element of the same
        /// array/list is disabled in the dropdown, so the collection cannot hold more than one
        /// instance of the same concrete type. Has no effect on a single (non-collection) field.
        /// Defaults to <c>true</c>.
        /// </summary>
        public bool AllowDuplicates { get; }

        public ManagedReferencePickerAttribute(bool allowDuplicates = true)
        {
            AllowDuplicates = allowDuplicates;
        }
    }
}
