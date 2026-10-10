using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;

namespace ManagedReferencePicker.Editor
{
    internal static class ManagedReferenceTypeDiscovery
    {
        public static List<ManagedReferenceTypeEntry> Discover()
        {
            var result = new List<ManagedReferenceTypeEntry>();
            var loaded = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies().Where(a => !a.IsDynamic)
                .GroupBy(a => a.GetName().Name).ToDictionary(g => g.Key, g => g.First());
            var sources = new Dictionary<string, List<ManagedReferenceTypeEntry>>(StringComparer.Ordinal);
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                if (!loaded.TryGetValue(assembly.name, out var runtimeAssembly))
                {
                    continue;
                }

                foreach (var file in assembly.sourceFiles)
                {
                    var path = file.Replace('\\', '/');
                    if (Path.IsPathRooted(path))
                    {
                        var root = Path.GetFullPath(".").Replace('\\', '/') + "/";
                        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        path = path.Substring(root.Length);
                    }

                    if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !File.Exists(path))
                    {
                        continue;
                    }

                    var guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid))
                    {
                        continue;
                    }

                    foreach (var declaration in GetDeclarations(File.ReadAllText(path)))
                    {
                        var type = runtimeAssembly.GetType(declaration, false);
                        if (type == null || !type.IsClass || type.IsAbstract || type.ContainsGenericParameters
                            || !Attribute.IsDefined(type, typeof(SerializableAttribute), false)
                            || typeof(UnityEngine.Object).IsAssignableFrom(type))
                        {
                            continue;
                        }

                        var entry = new ManagedReferenceTypeEntry
                        {
                            scriptGuid = guid,
                            scriptPath = path,
                            current = ManagedReferenceTypeIdentity.FromType(type)
                        };
                        if (!sources.TryGetValue(entry.current.Key, out var declarations))
                        {
                            declarations = new List<ManagedReferenceTypeEntry>();
                            sources.Add(entry.current.Key, declarations);
                        }

                        declarations.Add(entry);
                    }
                }
            }

            // A partial type has no unique source GUID; leave it for explicit migration metadata.
            foreach (var declarations in sources.Values)
            {
                if (declarations.Select(e => e.scriptGuid).Distinct().Count() == 1)
                {
                    result.Add(declarations[0]);
                }
            }

            return result.OrderBy(e => e.current.Key, StringComparer.Ordinal).ToList();
        }

        internal static List<string> GetDeclarations(string source)
        {
            var tokens = Tokenize(source);
            var result = new List<string>();
            var scopes = new Stack<(int depth, string ns, string parent)>();
            var depth = 0;
            var currentNamespace = string.Empty;
            var parent = string.Empty;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token == "namespace")
                {
                    var parts = new List<string>();
                    i++;
                    while (i < tokens.Count && tokens[i] != "{" && tokens[i] != ";")
                    {
                        parts.Add(tokens[i++].TrimStart('@'));
                    }

                    var next = string.Concat(parts);
                    if (i < tokens.Count && tokens[i] == "{")
                    {
                        scopes.Push((++depth, currentNamespace, parent));
                    }

                    currentNamespace = string.IsNullOrEmpty(currentNamespace) ? next : currentNamespace + "." + next;
                }
                else if (token == "class" || token == "struct" || token == "interface" || token == "record")
                {
                    // Exclude generic constraints such as "where T : class".
                    if (i + 1 >= tokens.Count || !Regex.IsMatch(tokens[i + 1], "^@?[\\p{L}_][\\p{L}\\p{N}_]*$"))
                    {
                        continue;
                    }

                    if (token == "record" && (tokens[i + 1] == "class" || tokens[i + 1] == "struct"))
                    {
                        i++;
                    }

                    var name = tokens[++i].TrimStart('@');
                    var generic = i + 1 < tokens.Count && tokens[i + 1] == "<";
                    var fullName = string.IsNullOrEmpty(parent) ? name : parent + "+" + name;
                    if (!generic)
                    {
                        result.Add(string.IsNullOrEmpty(currentNamespace) ? fullName : currentNamespace + "." + fullName);
                    }

                    var parentheses = 0;
                    while (i + 1 < tokens.Count)
                    {
                        var next = tokens[++i];
                        if (next == "(")
                        {
                            parentheses++;
                        }
                        else if (next == ")")
                        {
                            parentheses--;
                        }
                        else if (parentheses == 0 && (next == "{" || next == ";"))
                        {
                            if (next == "{")
                            {
                                scopes.Push((++depth, currentNamespace, parent));
                                parent = fullName;
                            }

                            break;
                        }
                    }
                }
                else if (token == "{")
                {
                    depth++;
                }
                else if (token == "}")
                {
                    if (scopes.Count > 0 && scopes.Peek().depth == depth)
                    {
                        var scope = scopes.Pop();
                        currentNamespace = scope.ns;
                        parent = scope.parent;
                    }

                    depth--;
                }
            }

            return result.Distinct().ToList();
        }

        private static List<string> Tokenize(string source)
        {
            var result = new List<string>();
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                if (c == '#' || (c == '/' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        i++;
                    }
                }
                else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && (source[i] != '*' || source[i + 1] != '/'))
                    {
                        i++;
                    }

                    i++;
                }
                else if (c == '"' || c == '\'')
                {
                    var verbatim = c == '"' && i > 0 && (source[i - 1] == '@'
                        || (source[i - 1] == '$' && i > 1 && source[i - 2] == '@'));
                    var quoteCount = 1;
                    if (c == '"')
                    {
                        while (i + quoteCount < source.Length && source[i + quoteCount] == '"')
                        {
                            quoteCount++;
                        }
                    }

                    if (quoteCount >= 3)
                    {
                        var end = source.IndexOf(new string('"', quoteCount), i + quoteCount, StringComparison.Ordinal);
                        i = end < 0 ? source.Length : end + quoteCount - 1;
                        continue;
                    }

                    while (++i < source.Length)
                    {
                        if (!verbatim && source[i] == '\\')
                        {
                            i++;
                        }
                        else if (source[i] == c)
                        {
                            if (verbatim && i + 1 < source.Length && source[i + 1] == c)
                            {
                                i++;
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }
                else if (char.IsLetter(c) || c == '_' || c == '@')
                {
                    var start = i;
                    while (i + 1 < source.Length && (char.IsLetterOrDigit(source[i + 1]) || source[i + 1] == '_'))
                    {
                        i++;
                    }

                    result.Add(source.Substring(start, i - start + 1));
                }
                else
                {
                    result.Add(c.ToString());
                }
            }

            return result;
        }
    }
}
