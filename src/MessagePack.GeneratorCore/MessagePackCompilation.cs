﻿// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MessagePackCompiler
{
    public class MessagePackCompilation
    {
        public static async Task<CSharpCompilation> CreateFromProjectAsync(string[] csprojs, string[] preprocessorSymbols, CancellationToken cancellationToken)
        {
            var parseOption = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse, SourceCodeKind.Regular, preprocessorSymbols);
            var syntaxTrees = new List<SyntaxTree>();
            var metadata = new[]
                {
                    typeof(object),
                    typeof(Enumerable),
                    typeof(Task<>),
                    typeof(IgnoreDataMemberAttribute),
                    typeof(System.Collections.Generic.List<>),
                    typeof(System.Collections.Concurrent.ConcurrentDictionary<,>),
                }
               .Select(x => x.Assembly.Location)
               .Distinct()
               .Select(x => MetadataReference.CreateFromFile(x))
               .ToList();

            var sources = new List<string>();
            var locations = new List<string>();
            foreach (var csproj in csprojs)
            {
                CollectDocument(csproj, sources, locations);
            }

            var hasAnnotations = false;
            foreach (var file in sources.Select(Path.GetFullPath).Distinct())
            {
                var text = File.ReadAllText(file.Replace('\\', Path.DirectorySeparatorChar), Encoding.UTF8);
                var syntax = CSharpSyntaxTree.ParseText(text, parseOption);
                syntaxTrees.Add(syntax);
                if (Path.GetFileNameWithoutExtension(file) == "Attributes")
                {
                    var root = await syntax.GetRootAsync(cancellationToken).ConfigureAwait(false);
                    if (root.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(x => x.Identifier.Text == "MessagePackObjectAttribute"))
                    {
                        hasAnnotations = true;
                    }
                }
            }

            if (!hasAnnotations)
            {
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(DummyAnnotation, parseOption));
            }

            var nazoloc = locations.Distinct();
            foreach (var item in locations.Distinct().Where(x => !x.Contains("MonoBleedingEdge")))
            {
                metadata.Add(MetadataReference.CreateFromFile(item));
            }

            var compilation = CSharpCompilation.Create(
                "MessagepackCodeGenTemp",
                syntaxTrees,
                metadata,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            return compilation;
        }

        private static void CollectDocument(string csproj, List<string> source, List<string> metadataLocations)
        {
            XDocument document;
            using (var sr = new StreamReader(csproj, true))
            {
                var reader = new XmlTextReader(sr);
                reader.Namespaces = false;

                document = XDocument.Load(reader, LoadOptions.None);
            }

            var csProjRoot = Path.GetDirectoryName(csproj);
            var framworkRoot = Path.GetDirectoryName(typeof(object).Assembly.Location);

            // Legacy
            // <Project ToolsVersion=...>
            // New
            // <Project Sdk="Microsoft.NET.Sdk">
            var proj = document.Element("Project");
            var legacyFormat = proj.Attribute("Sdk")?.Value != "Microsoft.NET.Sdk";

            if (!legacyFormat)
            {
                foreach (var file in IterateCsFileWithoutBinObj(Path.GetDirectoryName(csproj)))
                {
                    source.Add(file);
                }
            }

            {
                // files
                foreach (var item in document.Descendants("Compile"))
                {
                    var include = item.Attribute("Include")?.Value;
                    if (include != null)
                    {
                        // note: currently not supports Exclude
                        if (include.Contains("*"))
                        {
                            foreach (var item2 in IterateWildcardPath(csProjRoot, include))
                            {
                                source.Add(item2);
                            }
                        }
                        else
                        {
                            source.Add(Path.Combine(csProjRoot, include));
                        }
                    }
                }

                // shared
                foreach (var item in document.Descendants("Import"))
                {
                    if (item.Attribute("Label")?.Value == "Shared")
                    {
                        var sharedRoot = Path.GetDirectoryName(Path.Combine(csProjRoot, item.Attribute("Project").Value));
                        foreach (var file in IterateCsFileWithoutBinObj(Path.GetDirectoryName(sharedRoot)))
                        {
                            source.Add(file);
                        }
                    }
                }

                // proj-ref
                foreach (var item in document.Descendants("ProjectReference"))
                {
                    var refCsProjPath = item.Attribute("Include")?.Value;
                    if (refCsProjPath != null)
                    {
                        CollectDocument(Path.Combine(csProjRoot, refCsProjPath), source, metadataLocations);
                    }
                }

                // metadata
                foreach (var item in document.Descendants("Reference"))
                {
                    var hintPath = item.Element("HintPath")?.Value;
                    if (hintPath == null)
                    {
                        var path = Path.Combine(framworkRoot, item.Attribute("Include").Value + ".dll");
                        metadataLocations.Add(path);
                    }
                    else
                    {
                        metadataLocations.Add(Path.Combine(csProjRoot, hintPath));
                    }
                }

                // resolve NuGet reference
                foreach (var item in document.Descendants("PackageReference"))
                {
                    var nugetPackagesPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
                    if (nugetPackagesPath == null)
                    {
                        // Try default
                        // Windows: %userprofile%\.nuget\packages
                        // Mac/Linux: ~/.nuget/packages
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            nugetPackagesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".nuget\packages");
                        }
                        else
                        {
                            nugetPackagesPath = "~/nuget/packages";
                        }
                    }

                    var targetFramework = document.Descendants("TargetFramework").FirstOrDefault()?.Value ?? document.Descendants("TargetFrameworks").First().Value.Split(';').First();
                    var includePath = item.Attribute("Include").Value.Trim().ToLower(); // maybe lower
                    var packageVersion = item.Attribute("Version").Value.Trim();

                    var pathpath = Path.Combine(nugetPackagesPath, includePath, packageVersion, "lib", targetFramework);
                    if (!Directory.Exists(pathpath))
                    {
                        pathpath = pathpath.ToLower(); // try all lower.
                    }

                    if (Directory.Exists(pathpath))
                    {
                        foreach (var dllPath in Directory.GetFiles(pathpath, "*.dll"))
                        {
                            metadataLocations.Add(Path.GetFullPath(dllPath));
                        }
                    }
                }
            }
        }

        private static IEnumerable<string> IterateWildcardPath(string rootPath, string path)
        {
            var directory = new StringBuilder();
            foreach (var item in path.Split('\\', '/'))
            {
                // recursive
                if (item.Contains("**"))
                {
                    foreach (var item2 in Directory.GetDirectories(Path.Combine(rootPath, directory.ToString()), "*", SearchOption.AllDirectories))
                    {
                        foreach (var item3 in IterateWildcardPath(string.Empty, item2))
                        {
                            yield return item3;
                        }
                    }

                    yield break;
                }
                else if (item.Contains("*"))
                {
                    foreach (var item2 in Directory.GetFiles(Path.Combine(directory.ToString())))
                    {
                        if (Path.GetExtension(item) == ".cs")
                        {
                            yield return item2;
                        }
                    }

                    yield break;
                }
                else if (!(item == "obj" || item == "bin"))
                {
                    if (directory.Length != 0)
                    {
                        directory.Append(Path.DirectorySeparatorChar);
                    }

                    directory.Append(item);
                }
            }

            var finalPath = directory.ToString();
            if (File.Exists(finalPath))
            {
                yield return finalPath;
            }
            else
            {
                foreach (var item in Directory.GetFiles(finalPath))
                {
                    if (Path.GetExtension(item) == ".cs")
                    {
                        yield return item;
                    }
                }
            }
        }

        public static async Task<CSharpCompilation> CreateFromDirectoryAsync(string directoryRoot, string[] preprocessorSymbols, CancellationToken cancellationToken)
        {
            var parseOption = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse, SourceCodeKind.Regular, preprocessorSymbols);

            var hasAnnotations = false;
            var syntaxTrees = new List<SyntaxTree>();
            foreach (var file in IterateCsFileWithoutBinObj(directoryRoot))
            {
                var text = File.ReadAllText(file.Replace('\\', Path.DirectorySeparatorChar), Encoding.UTF8);
                var syntax = CSharpSyntaxTree.ParseText(text, parseOption);
                syntaxTrees.Add(syntax);
                if (Path.GetFileNameWithoutExtension(file) == "Attributes")
                {
                    var root = await syntax.GetRootAsync(cancellationToken).ConfigureAwait(false);
                    if (root.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(x => x.Identifier.Text == "MessagePackObjectAttribute"))
                    {
                        hasAnnotations = true;
                    }
                }
            }

            if (!hasAnnotations)
            {
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(DummyAnnotation, parseOption));
            }

            var metadata = new[]
                {
                    typeof(object),
                    typeof(Enumerable),
                    typeof(Task<>),
                    typeof(IgnoreDataMemberAttribute),
                    typeof(System.Collections.Generic.List<>),
                    typeof(System.Collections.Concurrent.ConcurrentDictionary<,>),
                }
                .Select(x => x.Assembly.Location)
                .Distinct()
                .Select(x => MetadataReference.CreateFromFile(x))
                .ToList();

            var compilation = CSharpCompilation.Create(
                "MessagepackCodeGenTemp",
                syntaxTrees,
                metadata,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            return compilation;
        }

        private static IEnumerable<string> IterateCsFileWithoutBinObj(string root)
        {
            foreach (var item in Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
            {
                yield return item;
            }

            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var dirName = new DirectoryInfo(dir).Name;
                if (dirName == "bin" || dirName == "obj")
                {
                    continue;
                }

                foreach (var item in IterateCsFileWithoutBinObj(dir))
                {
                    yield return item;
                }
            }
        }

        private const string DummyAnnotation = @"
using System;

namespace MessagePack
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
    public class MessagePackObjectAttribute : Attribute
    {
        public bool KeyAsPropertyName { get; private set; }

        public MessagePackObjectAttribute(bool keyAsPropertyName = false)
        {
            this.KeyAsPropertyName = keyAsPropertyName;
        }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public class KeyAttribute : Attribute
    {
        public int? IntKey { get; private set; }
        public string StringKey { get; private set; }

        public KeyAttribute(int x)
        {
            this.IntKey = x;
        }

        public KeyAttribute(string x)
        {
            this.StringKey = x;
        }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public class IgnoreMemberAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public class UnionAttribute : Attribute
    {
        public int Key { get; private set; }
        public Type SubType { get; private set; }

        public UnionAttribute(int key, Type subType)
        {
            this.Key = key;
            this.SubType = subType;
        }
    }

    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = true)]
    public class SerializationConstructorAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class MessagePackFormatterAttribute : Attribute
    {
        public Type FormatterType { get; private set; }
        public object[] Arguments { get; private set; }

        public MessagePackFormatterAttribute(Type formatterType)
        {
            this.FormatterType = formatterType;
        }

        public MessagePackFormatterAttribute(Type formatterType, params object[] arguments)
        {
            this.FormatterType = formatterType;
            this.Arguments = arguments;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace MessagePack
{
    public interface IMessagePackSerializationCallbackReceiver
    {
        void OnBeforeSerialize();
        void OnAfterDeserialize();
    }
}
";
    }
}
