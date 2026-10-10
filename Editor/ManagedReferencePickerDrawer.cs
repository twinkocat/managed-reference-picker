using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;

using UnityEngine;

namespace ManagedReferencePicker.Editor
{
    /// <summary>
    /// Per-element drawer for <see cref="ManagedReferencePickerAttribute"/>. Renders a type
    /// dropdown next to a foldout: choosing a type instantiates the concrete subclass into the
    /// <see cref="SerializeReference"/> slot, and the instance's serialized fields are drawn below.
    /// </summary>
    [CustomPropertyDrawer(typeof(ManagedReferencePickerAttribute))]
    public class ManagedReferencePickerDrawer : PropertyDrawer
    {
        // baseType.FullName -> concrete candidate types (cached per domain reload).
        private static readonly Dictionary<string, List<Type>> candidateCache = new();

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.ManagedReference)
            {
                EditorGUI.PropertyField(position, property, label, true);
                return;
            }

            EditorGUI.BeginProperty(position, label, property);

            var lineHeight = EditorGUIUtility.singleLineHeight;
            var current = ResolveType(property.managedReferenceFullTypename);

            var foldoutRect = new Rect(position.x, position.y, EditorGUIUtility.labelWidth, lineHeight);
            property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);

            var dropdownRect = new Rect(position.x + EditorGUIUtility.labelWidth, position.y,
                position.width - EditorGUIUtility.labelWidth, lineHeight);
            var display = current != null ? DisplayName(current) : "(None)";
            if (EditorGUI.DropdownButton(dropdownRect, new GUIContent(display), FocusType.Keyboard))
            {
                ShowTypeMenu(property, dropdownRect);
            }

            if (property.isExpanded && current != null)
            {
                EditorGUI.indentLevel++;
                var y = position.y + lineHeight + EditorGUIUtility.standardVerticalSpacing;
                foreach (var child in EnumerateChildren(property))
                {
                    var h = EditorGUI.GetPropertyHeight(child, true);
                    EditorGUI.PropertyField(new Rect(position.x, y, position.width, h), child, true);
                    y += h + EditorGUIUtility.standardVerticalSpacing;
                }

                EditorGUI.indentLevel--;
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.ManagedReference)
            {
                return EditorGUI.GetPropertyHeight(property, label, true);
            }

            var height = EditorGUIUtility.singleLineHeight;
            if (property.isExpanded && ResolveType(property.managedReferenceFullTypename) != null)
            {
                foreach (var child in EnumerateChildren(property))
                {
                    height += EditorGUI.GetPropertyHeight(child, true) + EditorGUIUtility.standardVerticalSpacing;
                }
            }

            return height;
        }

        private static IEnumerable<SerializedProperty> EnumerateChildren(SerializedProperty property)
        {
            var iterator = property.Copy();
            var end = iterator.GetEndProperty();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;
                yield return iterator.Copy();
            }
        }

        private void ShowTypeMenu(SerializedProperty property, Rect rect)
        {
            var baseType = ResolveType(property.managedReferenceFieldTypename);
            var propertyCopy = property.Copy();

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("(None)"), false, () => Assign(propertyCopy, null));

            foreach (var type in GetCandidates(baseType))
            {
                var captured = type;
                menu.AddItem(new GUIContent(DisplayName(type)), false, () => Assign(propertyCopy, captured));
            }

            menu.DropDown(rect);
        }

        private static void Assign(SerializedProperty property, Type type)
        {
            property.serializedObject.Update();
            property.managedReferenceValue = type != null ? Activator.CreateInstance(type) : null;
            property.serializedObject.ApplyModifiedProperties();
        }

        private static IEnumerable<Type> GetCandidates(Type baseType)
        {
            if (baseType == null)
            {
                return Array.Empty<Type>();
            }

            if (candidateCache.TryGetValue(baseType.FullName!, out var cached))
            {
                return cached;
            }

            var candidates = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies().SelectMany(SafeGetTypes)
                .Where(t => t != null
                            && !t.IsAbstract
                            && !t.IsInterface
                            && !t.IsGenericTypeDefinition
                            && baseType.IsAssignableFrom(t)
                            && t.GetConstructor(Type.EmptyTypes) != null
                            && !Attribute.IsDefined(t, typeof(HideInManagedReferencePickerAttribute), false))
                .OrderBy(DisplayName, StringComparer.Ordinal)
                .ToList();

            candidateCache[baseType.FullName!] = candidates;
            return candidates;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null);
            }
        }

        private static string DisplayName(Type type)
        {
            var menu = type.GetCustomAttribute<ManagedReferenceNameAttribute>();
            if (menu != null && !string.IsNullOrEmpty(menu.DisplayName))
            {
                return menu.DisplayName;
            }

            var name = type.Name;
            if (name.EndsWith("Component", StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - "Component".Length);
            }

            return ObjectNames.NicifyVariableName(name);
        }

        // Unity's managed-reference typenames are formatted as "<assemblyName> <fullTypeName>".
        private static Type ResolveType(string managedReferenceTypename)
        {
            if (string.IsNullOrEmpty(managedReferenceTypename))
            {
                return null;
            }

            var parts = managedReferenceTypename.Split(' ');
            if (parts.Length != 2)
            {
                return null;
            }

            return Type.GetType($"{parts[1]}, {parts[0]}");
        }
    }
}
