#define EMIT
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Configuration;
using System.Web.Razor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace StackExchange.Precompiler
{
    class Compilation
    {
        private readonly CSharpCommandLineArguments _cscArgs;
        private readonly DirectoryInfo _currentDirectory;
        private readonly Encoding _encoding;
        private readonly WebConfigurationFileMap _configMap;

        public Compilation(CSharpCommandLineArguments cscArgs, DirectoryInfo currentDirectory, Encoding defaultEncoding = null)
        {
            _cscArgs = cscArgs;
            _currentDirectory = currentDirectory;
            _encoding = _cscArgs.Encoding ?? defaultEncoding ?? Encoding.UTF8;

            _configMap = new WebConfigurationFileMap { VirtualDirectories = { { "/", new VirtualDirectoryMapping(currentDirectory.FullName, true) } } };

            AppDomain.CurrentDomain.SetData("DataDirectory", Path.Combine(currentDirectory.FullName, "App_Data")); // HACK mocking ASP.NET's ~/App_Data aka. |DataDirectory|
        }

        public void Run()
        {
            var references = SetupReferences();
            var sources = LoadSources(_cscArgs.SourceFiles.Select(x => x.Path).ToArray());

            var diagnostics = new List<Diagnostic>();
            var compilationModules = LoadModules(diagnostics).ToList();

            var compilation = CSharpCompilation.Create(
                options: _cscArgs.CompilationOptions,
                references: references,
                syntaxTrees: sources,
                assemblyName: _cscArgs.CompilationName);

            diagnostics.AddRange(compilation.GetDiagnostics());

            var context = new BeforeCompileContext()
            {
                Modules = compilationModules,
                Arguments = _cscArgs,
                Compilation = compilation,
                Diagnostics = diagnostics,
                Resources = _cscArgs.ManifestResources.ToList()
            };

            foreach (var module in compilationModules)
            {
                module.BeforeCompile(context);
            }

            Emit(context);
        }

        private IEnumerable<ICompileModule> LoadModules(List<Diagnostic> diagnostics)
        {
            var compilationSection = PrecompilerSection.Current;
            if (compilationSection == null) yield break;

            foreach(var module in compilationSection.CompileModules.Cast<CompileModuleElement>())
            {
                ICompileModule compileModule = null;
                try
                {
                    var type = Type.GetType(module.Type, false);
                    compileModule = Activator.CreateInstance(type, true) as ICompileModule;
                }
                catch(Exception ex)
                {
                    diagnostics.Add(Diagnostic.Create(
                        new DiagnosticDescriptor("MOONCOMP001", "MOONCOMP001", "Failed to create ICompileModule implementation '{0}': {1}", "MoonSpeak.Compilation", DiagnosticSeverity.Error, true),
                        Location.Create(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile, new TextSpan(), new LinePositionSpan()),
                        module.Type,
                        ex.Message));
                }
                if (compileModule != null)
                {
                    yield return compileModule;
                }
            }
        }

        private void Emit(BeforeCompileContext beforeContext)
        {
            var compilation = beforeContext.Compilation;
            var pdbPath = _cscArgs.PdbPath;
            var outputPath = Path.Combine(_cscArgs.OutputDirectory, _cscArgs.OutputFileName);

            if (_cscArgs.EmitPdb)
            {
                pdbPath = null;
            }
            else if (string.IsNullOrWhiteSpace(pdbPath))
            {
                pdbPath = Path.ChangeExtension(outputPath, ".pdb");
            }

            using (var peStream = File.Create(outputPath))
            using (var pdbStream = !string.IsNullOrWhiteSpace(pdbPath) ? File.Create(pdbPath) : null)
            using (var xmlDocumentationStream = !string.IsNullOrWhiteSpace(_cscArgs.DocumentationPath) ? File.Create(_cscArgs.DocumentationPath) : null)
            using (var win32Resources = compilation.CreateDefaultWin32Resources(
                versionResource: true,
                noManifest: _cscArgs.NoWin32Manifest,
                manifestContents: !string.IsNullOrWhiteSpace(_cscArgs.Win32Manifest) ? new MemoryStream(File.ReadAllBytes(_cscArgs.Win32Manifest)) : null,
                iconInIcoFormat: !string.IsNullOrWhiteSpace(_cscArgs.Win32Icon) ? new MemoryStream(File.ReadAllBytes(_cscArgs.Win32Icon)) : null))
            {
                var emitResult = compilation.Emit(
                    peStream: peStream,
                    pdbStream: pdbStream,
                    xmlDocumentationStream: xmlDocumentationStream,
                    options: _cscArgs.EmitOptions,
                    manifestResources: _cscArgs.ManifestResources,
                    win32Resources: win32Resources);

                ((List<Diagnostic>)beforeContext.Diagnostics).AddRange(emitResult.Diagnostics);
                var afterContext = new AfterCompileContext
                {
                    Arguments = _cscArgs,
                    AssemblyStream = peStream,
                    Compilation = compilation,
                    Diagnostics = beforeContext.Diagnostics,
                    SymbolStream = pdbStream,
                    XmlDocStream = xmlDocumentationStream,
                };

                foreach(var module in beforeContext.Modules)
                {
                    module.AfterCompile(afterContext);
                }

                foreach (var diag in afterContext.Diagnostics)
                {
                    Console.WriteLine(diag.ToString());
                }
                if (!emitResult.Success)
                {
                    throw GetMsBuildError(text: "Compilation failed");
                }
            }
        }

        class BeforeCompileContext : IBeforeCompileContext
        {
            internal List<ICompileModule> Modules { get; set; }
            public CSharpCommandLineArguments Arguments { get; set; }
            public CSharpCompilation Compilation { get; set; }
            public IList<ResourceDescription> Resources { get; set; }
            public IList<Diagnostic> Diagnostics { get; set; }
        }

        class AfterCompileContext : IAfterCompileContext
        {
            public CSharpCommandLineArguments Arguments { get; set; }
            public CSharpCompilation Compilation { get; set; }
            public Stream AssemblyStream { get; set; }
            public Stream SymbolStream { get; set; }
            public Stream XmlDocStream { get; set; }
            public IList<Diagnostic> Diagnostics { get; set; }
        }

        private ICollection<MetadataReference> SetupReferences()
        {
            var references = _cscArgs.MetadataReferences.Select(x => (MetadataReference)MetadataReference.CreateFromFile(x.Reference, x.Properties)).ToArray();
            var referenceAssemblies = references.Select(x =>
            {
                try
                {
                    return Assembly.ReflectionOnlyLoadFrom(x.Display);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("warning: could not load reference '{0}' - {1} - {2}", x.Display, ex.Message, ex.StackTrace);
                    return null;
                }
            }).Where(x => x != null).ToArray();
            var fullLookup = referenceAssemblies.ToDictionary(x => x.FullName);
            var shortLookup = referenceAssemblies.ToDictionary(x => x.GetName().Name);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                Assembly a;
                if (fullLookup.TryGetValue(e.Name, out a) || shortLookup.TryGetValue(e.Name, out a))
                {
                    return Assembly.LoadFile(a.Location);
                }
                return null;
            };
            return references;
        }

        private SyntaxTree ParseSyntaxTreeAndDispose(Stream stream, string path, Encoding encoding = null)
        {
            try
            {
                var sourceText = SourceText.From(stream, encoding ?? _encoding);
                return SyntaxFactory.ParseSyntaxTree(sourceText, _cscArgs.ParseOptions, path);
            }
            catch (Exception ex)
            {
                throw GetMsBuildError(path, text: ex.Message);
            }
            finally
            {
                stream.Dispose();
            }
        }

        private SyntaxTree ParseSyntaxTree(string source, string path, Encoding encoding = null)
        {
            encoding = encoding ?? _encoding;
            return ParseSyntaxTreeAndDispose(new MemoryStream(encoding.GetBytes(source)), path, encoding);
        }

        private SyntaxTree[] LoadSources(ICollection<string> paths)
        {
            var trees = new SyntaxTree[paths.Count];
            Parallel.ForEach(paths,
                (file, state, index) =>
                {
                    switch (Path.GetExtension(file))
                    {
                        case ".cs":
                            trees[index] = ParseSyntaxTreeAndDispose(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read), file);
                            break;
                        case ".cshtml":
                            var viewFullPath = file;
                            var viewVirtualPath = GetRelativeUri(file, _currentDirectory.FullName);
                            try
                            {
                                var viewConfig = WebConfigurationManager.OpenMappedWebConfiguration(_configMap, viewVirtualPath);
                                var razorConfig = viewConfig.GetSectionGroup("system.web.webPages.razor") as System.Web.WebPages.Razor.Configuration.RazorWebSectionGroup;
                                var host = razorConfig == null
                                    ? System.Web.WebPages.Razor.WebRazorHostFactory.CreateDefaultHost(viewVirtualPath, viewFullPath)
                                    : System.Web.WebPages.Razor.WebRazorHostFactory.CreateHostFromConfig(razorConfig, viewVirtualPath, viewFullPath);
                                var sbSource = new StringBuilder();
                                using (var str = new FileStream(viewFullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                                using (var rdr = new StreamReader(str, _encoding, detectEncodingFromByteOrderMarks: true))
                                using (var provider = CodeDomProvider.CreateProvider("csharp"))
                                using (var typeWr = new StringWriter(sbSource))
                                {
                                    var engine = new RazorTemplateEngine(host);
                                    var className = Regex.Replace(viewVirtualPath, @"[^\w]", "_", RegexOptions.IgnoreCase);
                                    var razorOut = engine.GenerateCode(rdr, className, "ASP", viewFullPath);
                                    var codeGenOptions = new CodeGeneratorOptions { VerbatimOrder = true, ElseOnClosing = false, BlankLinesBetweenMembers = false };
                                    provider.GenerateCodeFromCompileUnit(razorOut.GeneratedCode, typeWr, codeGenOptions);
                                    trees[index] = ParseSyntaxTree(sbSource.ToString(), viewFullPath, rdr.CurrentEncoding);
                                }
                            }
                            catch (Exception ex)
                            {
                                throw GetMsBuildError(viewVirtualPath, text: ex.Message);
                            }
                            break;
                        default:
                            throw GetMsBuildError(file, text: "unknown file type");
                    }
                });
            return trees;
        }

        private static string GetRelativeUri(string filespec, string folder)
        {
            Uri pathUri = new Uri(filespec);
            if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                folder += Path.DirectorySeparatorChar;
            }
            Uri folderUri = new Uri(folder);
            return "/" + folderUri.MakeRelativeUri(pathUri).ToString().TrimStart('/');
        }

        // http://blogs.msdn.com/b/msbuild/archive/2006/11/03/msbuild-visual-studio-aware-error-messages-and-message-formats.aspx
        // http://msdn.microsoft.com/en-us/library/yxkt8b26.aspx
        public static CompilationFailedException GetMsBuildError(string origin = null, LinePosition? position = null, string code = "MOONSPEAKCOMPILER", string text = null, string severity = "error")
        {
            origin = origin ?? Path.GetFileName(typeof(Compilation).Assembly.Location);
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(origin))
            {
                sb.Append(origin);
                if (position.HasValue)
                {
                    sb.AppendFormat("({0},{1})", position.Value.Line + 1, position.Value.Character + 1);
                }
                sb.Append(": ");
            }
            sb.AppendFormat("{0} {1}: {2}", severity, code, text);
            return new CompilationFailedException(sb.ToString())
            {
                Data =
                {
                    { "code", code },
                    { "origin", origin },
                    { "originPosition", position + "" },
                    { "text", text },
                },
            };
        }
    }
}