﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Decompiler.Ast;
using ICSharpCode.Decompiler.Ast.Transforms;
using ICSharpCode.Decompiler.ILAst;
using ICSharpCode.NRefactory.CSharp;
using JSIL.Ast;
using JSIL.Internal;
using JSIL.Transforms;
using JSIL.Translator;
using Mono.Cecil;
using ICSharpCode.Decompiler;
using Mono.Cecil.Pdb;
using Mono.Cecil.Cil;

namespace JSIL {
    public class AssemblyTranslator {
        public const int LargeMethodThreshold = 20 * 1024;

        public readonly Configuration Configuration;
        public readonly TypeInfoProvider TypeInfoProvider;

        public readonly SymbolProvider SymbolProvider = new SymbolProvider();
        public readonly FunctionCache FunctionCache = new FunctionCache();
        public readonly AssemblyManifest Manifest = new AssemblyManifest();

        public event Action<string> AssemblyLoaded;

        public event Action<ProgressReporter> Decompiling;
        public event Action<ProgressReporter> Optimizing;
        public event Action<ProgressReporter> Writing;
        public event Action<string, ProgressReporter> DecompilingMethod;

        public event Action<string, Exception> CouldNotLoadSymbols;
        public event Action<string, Exception> CouldNotResolveAssembly;
        public event Action<string, Exception> CouldNotDecompileMethod;

        public AssemblyTranslator (
            Configuration configuration,
            TypeInfoProvider typeInfoProvider = null
        ) {
            Configuration = configuration;
            // Important to avoid preserving the proxy list from previous translations in this process
            MemberIdentifier.ResetProxies();

            if (typeInfoProvider != null) {
                TypeInfoProvider = typeInfoProvider;

                if (configuration.Assemblies.Proxies.Count > 0)
                    throw new InvalidOperationException("Cannot reuse an existing type provider if explicitly loading proxies");
            } else {
                TypeInfoProvider = new JSIL.TypeInfoProvider();

                Assembly proxyAssembly = null;
                var proxyPath = Path.GetDirectoryName(Util.GetPathOfAssembly(Assembly.GetExecutingAssembly()));

                if (!configuration.FrameworkVersion.HasValue || configuration.FrameworkVersion == 4.0) {
                    proxyAssembly = Assembly.LoadFile(Path.Combine(proxyPath, "JSIL.Proxies.4.0.dll"));
                } else if (configuration.FrameworkVersion <= 3.5) {
                    proxyAssembly = Assembly.LoadFile(Path.Combine(proxyPath, "JSIL.Proxies.3.5.dll"));
                } else {
                    throw new ArgumentOutOfRangeException("FrameworkVersion", "Framework version not supported");
                }

                if (proxyAssembly == null)
                    throw new InvalidOperationException("No core proxy assembly was loaded.");

                AddProxyAssembly(proxyAssembly);

                foreach (var fn in configuration.Assemblies.Proxies)
                    AddProxyAssembly(fn);
            }
        }

        protected virtual ReaderParameters GetReaderParameters (bool useSymbols, string mainAssemblyPath = null) {
            var readerParameters = new ReaderParameters {
                ReadingMode = ReadingMode.Deferred,
                ReadSymbols = useSymbols
            };

            if (mainAssemblyPath != null) {
                readerParameters.AssemblyResolver = new AssemblyResolver(new string[] { 
                    Path.GetDirectoryName(mainAssemblyPath),
                    Path.GetDirectoryName(Util.GetPathOfAssembly(Assembly.GetExecutingAssembly())) 
                });
            }

            if (useSymbols)
                readerParameters.SymbolReaderProvider = SymbolProvider;

            return readerParameters;
        }

        public void AddProxyAssembly (string path) {
            var assemblies = LoadAssembly(path, Configuration.UseSymbols.GetValueOrDefault(true), false);

            TypeInfoProvider.AddProxyAssemblies(assemblies);
        }

        public void AddProxyAssembly (Assembly assembly) {
            var path = Util.GetPathOfAssembly(assembly);

            AddProxyAssembly(path);
        }

        public AssemblyDefinition[] LoadAssembly (string path) {
            return LoadAssembly(
                path, 
                Configuration.UseSymbols.GetValueOrDefault(true), 
                Configuration.IncludeDependencies.GetValueOrDefault(true)
            );
        }

        protected AssemblyDefinition AssemblyLoadErrorWrapper<T> (
            Func<T, ReaderParameters, AssemblyDefinition> loader,
            T arg1, ReaderParameters readerParameters, 
            bool useSymbols, string mainAssemblyPath
        ) {
            AssemblyDefinition result = null;

            try {
                result = loader(arg1, readerParameters);
            } catch (Exception ex) {
                if (useSymbols) {
                    try {
                        result = loader(arg1, GetReaderParameters(false, mainAssemblyPath));
                        if (CouldNotLoadSymbols != null)
                            CouldNotLoadSymbols(arg1.ToString(), ex);
                    } catch (Exception ex2) {
                        if (CouldNotResolveAssembly != null)
                            CouldNotResolveAssembly(arg1.ToString(), ex2);
                    }
                } else {
                    if (CouldNotResolveAssembly != null)
                        CouldNotResolveAssembly(arg1.ToString(), ex);
                }
            }

            return result;
        }

        protected ParallelOptions GetParallelOptions () {
            return new ParallelOptions {
                MaxDegreeOfParallelism = Configuration.UseThreads.GetValueOrDefault(true) ? -1 : 1
            };
        }

        protected AssemblyDefinition[] LoadAssembly (string path, bool useSymbols, bool includeDependencies) {
            if (String.IsNullOrWhiteSpace(path))
                throw new InvalidDataException("Path was empty.");

            var readerParameters = GetReaderParameters(useSymbols, path);

            var assembly = AssemblyLoadErrorWrapper(
                AssemblyDefinition.ReadAssembly,
                path, readerParameters, 
                useSymbols, path
            );

            var result = new List<AssemblyDefinition>();
            result.Add(assembly);

            if (AssemblyLoaded != null)
                AssemblyLoaded(path);

            if (includeDependencies) {
                var parallelOptions = GetParallelOptions();
                var modulesToVisit = new List<ModuleDefinition>(assembly.Modules);
                var assembliesToLoad = new List<AssemblyNameReference>();
                var visitedModules = new HashSet<string>();
                var assemblyNames = new HashSet<string>();

                while ((modulesToVisit.Count > 0) || (assembliesToLoad.Count > 0)) {
                    foreach (var module in modulesToVisit) {
                        if (visitedModules.Contains(module.FullyQualifiedName))
                            continue;

                        visitedModules.Add(module.FullyQualifiedName);

                        foreach (var reference in module.AssemblyReferences) {
                            bool ignored = false;
                            foreach (var ia in Configuration.Assemblies.Ignored) {
                                if (Regex.IsMatch(reference.FullName, ia, RegexOptions.IgnoreCase)) {
                                    ignored = true;
                                    break;
                                }
                            }

                            if (ignored)
                                continue;
                            if (assemblyNames.Contains(reference.FullName))
                                continue;

                            assemblyNames.Add(reference.FullName);
                            assembliesToLoad.Add(reference);
                        }
                    }
                    modulesToVisit.Clear();

                    Parallel.For(
                        0, assembliesToLoad.Count, parallelOptions, (i) => {
                            var anr = assembliesToLoad[i];

                            var childParameters = new ReaderParameters {
                                ReadingMode = ReadingMode.Deferred,
                                ReadSymbols = true,
                                SymbolReaderProvider = SymbolProvider
                            };

                            AssemblyDefinition refAssembly = null;
                            refAssembly = AssemblyLoadErrorWrapper(
                                readerParameters.AssemblyResolver.Resolve,
                                anr, readerParameters,
                                useSymbols, path
                            );

                            if (AssemblyLoaded != null)
                                AssemblyLoaded(refAssembly.MainModule.FullyQualifiedName);

                            if (refAssembly != null) {
                                lock (result)
                                    result.Add(refAssembly);

                                lock (modulesToVisit)
                                    modulesToVisit.AddRange(refAssembly.Modules);
                            }
                        }
                    );
                    assembliesToLoad.Clear();
                }
            }

            return result.ToArray();
        }

        protected DecompilerContext MakeDecompilerContext (ModuleDefinition module) {
            var context = new DecompilerContext(module);

            context.Settings.YieldReturn = false;
            context.Settings.AnonymousMethods = true;
            context.Settings.QueryExpressions = false;
            context.Settings.LockStatement = false;
            context.Settings.FullyQualifyAmbiguousTypeNames = true;
            context.Settings.ForEachStatement = false;

            return context;
        }

        public TranslationResult Translate (string assemblyPath, bool scanForProxies = true) {
            var result = new TranslationResult();
            var assemblies = LoadAssembly(assemblyPath);
            var parallelOptions = GetParallelOptions();

            if (scanForProxies)
                TypeInfoProvider.AddProxyAssemblies(assemblies);

            var pr = new ProgressReporter();
            if (Decompiling != null)
                Decompiling(pr);

            var methodsToAnalyze = new ConcurrentBag<MethodDefinition>();
            for (int i = 0; i < assemblies.Length; i++) {
                pr.OnProgressChanged(i, assemblies.Length * 2);
                GetMethodsToAnalyze(assemblies[i], methodsToAnalyze);
            }

            {
                int i = 0, mc = methodsToAnalyze.Count;
                Parallel.For(
                    0, methodsToAnalyze.Count, parallelOptions,
                    () => MakeDecompilerContext(assemblies[0].MainModule),
                    (_, loopState, ctx) => {
                        MethodDefinition m;
                        if (!methodsToAnalyze.TryTake(out m))
                            throw new InvalidOperationException("Method collection mutated during analysis");

                        ctx.CurrentModule = m.Module;
                        ctx.CurrentType = m.DeclaringType;
                        ctx.CurrentMethod = m;

                        TranslateMethodExpression(ctx, m, m);

                        var j = Interlocked.Increment(ref i);
                        pr.OnProgressChanged(mc + j, mc * 2);

                        return ctx;
                    },
                    (ctx) => { }
                );
            }

            pr.OnFinished();

            OptimizeAll();

            pr = new ProgressReporter();
            if (Writing != null)
                Writing(pr);

            Parallel.For(
                0, assemblies.Length, parallelOptions, (i) => {
                    var assembly = assemblies[i];
                    var outputPath = assembly.Name + ".js";

                    using (var outputStream = new MemoryStream()) {
                        var context = MakeDecompilerContext(assembly.MainModule);
                        Translate(context, assembly, outputStream);

                        var segment = new ArraySegment<byte>(
                            outputStream.GetBuffer(), 0, (int)outputStream.Length
                        );

                        lock (result.Files)
                            result.Files[outputPath] = segment;
                    }

                    lock (result.Assemblies)
                        result.Assemblies.Add(assembly);

                    pr.OnProgressChanged(result.Assemblies.Count, assemblies.Length);
                }
            );

            pr.OnFinished();

            using (var ms = new MemoryStream())
            using (var tw = new StreamWriter(ms, new UTF8Encoding(false))) {
                tw.WriteLine("// {0} {1}", GetHeaderText(), Environment.NewLine);

                foreach (var kvp in Manifest.Entries) {
                    tw.WriteLine(
                        "var {0} = JSIL.GetAssembly({1});",
                        kvp.Key, Util.EscapeString(kvp.Value, '\"')
                    );
                }

                tw.Flush();
                result.Manifest = new ArraySegment<byte>(
                    ms.GetBuffer(), 0, (int)ms.Length
                );
            }

            return result;
        }

        protected void OptimizeAll () {
            var pr = new ProgressReporter();
            if (Optimizing != null)
                Optimizing(pr);

            int i = 0;
            QualifiedMemberIdentifier id;
            while (FunctionCache.OptimizationQueue.TryDequeue(out id)) {
                var e = FunctionCache.GetCacheEntry(id);
                if (e.Expression == null) {
                    i++;
                    continue;
                }

                pr.OnProgressChanged(i++, i + FunctionCache.OptimizationQueue.Count);
                OptimizeFunction(e.SpecialIdentifiers, e.ParameterNames, e.Variables, e.Expression);
            }

            pr.OnFinished();
        }

        protected void GetMethodsToAnalyze (AssemblyDefinition assembly, ConcurrentBag<MethodDefinition> allMethods) {
            bool isStubbed = IsStubbed(assembly);

            var parallelOptions = GetParallelOptions();
            var allTypes = new List<TypeDefinition>();

            foreach (var module in assembly.Modules) {
                var moduleInfo = TypeInfoProvider.GetModuleInformation(module);
                if (moduleInfo.IsIgnored)
                    continue;

                allTypes.AddRange(module.Types);
            }

            while (allTypes.Count > 0) {
                var types = allTypes.ToArray();
                allTypes.Clear();

                Parallel.For(
                    0, types.Length, parallelOptions, (i) => {
                        var type = types[i];

                        lock (allTypes)
                            allTypes.AddRange(type.NestedTypes);

                        if (!ShouldTranslateMethods(type))
                            return;

                        var methods = (from m in type.Methods
                                    select m);

                        foreach (var m in type.Methods) {
                            if (!m.HasBody)
                                continue;

                            var mi = TypeInfoProvider.GetMethod(m);

                            if ((mi == null) || (mi.IsIgnored))
                                continue;

                            if (isStubbed) {
                                var isProperty = mi.DeclaringProperty != null;

                                if (!(isProperty && m.IsCompilerGenerated()))
                                    continue;
                            }

                            allMethods.Add(m);
                        }
                    }
                );
            }
        }

        protected bool IsStubbed (AssemblyDefinition assembly) {
            bool stubbed = false;
            foreach (var sa in Configuration.Assemblies.Stubbed) {
                if (Regex.IsMatch(assembly.FullName, sa, RegexOptions.IgnoreCase)) {
                    return true;
                    break;
                }
            }

            return false;
        }

        protected string GetHeaderText () {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return String.Format(
                "Generated by JSIL v{0}.{1}.{2} build {3}. See http://jsil.org/ for more information.",
                version.Major, version.Minor, version.Build, version.Revision
            );
        }

        protected void Translate (DecompilerContext context, AssemblyDefinition assembly, Stream outputStream) {
            bool stubbed = IsStubbed(assembly);

            var tw = new StreamWriter(outputStream, Encoding.ASCII);
            var formatter = new JavascriptFormatter(tw, this.TypeInfoProvider, Manifest, assembly);

            formatter.Comment(GetHeaderText());
            formatter.NewLine();

            if (stubbed) {
                formatter.Comment("Generating type stubs only");
                formatter.NewLine();
            }

            formatter.DeclareAssembly();

            var sealedTypes = new HashSet<TypeDefinition>();
            var declaredTypes = new HashSet<TypeDefinition>();

            foreach (var module in assembly.Modules)
                TranslateModule(context, formatter, module, sealedTypes, declaredTypes, stubbed);

            tw.Flush();
        }

        protected void TranslateModule (
            DecompilerContext context, JavascriptFormatter output, ModuleDefinition module, 
            HashSet<TypeDefinition> sealedTypes, HashSet<TypeDefinition> declaredTypes, bool stubbed
        ) {
            var moduleInfo = TypeInfoProvider.GetModuleInformation(module);
            if (moduleInfo.IsIgnored)
                return;

            context.CurrentModule = module;

            var js = new JSSpecialIdentifiers(context.CurrentModule.TypeSystem);
            var jsil = new JSILIdentifier(context.CurrentModule.TypeSystem, js);

            // Probably should be an argument, not a member variable...
            var astEmitter = new JavascriptAstEmitter(
                output, jsil, context.CurrentModule.TypeSystem, this.TypeInfoProvider
            );

            foreach (var typedef in module.Types)
                DeclareType(context, typedef, astEmitter, output, declaredTypes, stubbed);
        }

        protected void TranslateInterface (DecompilerContext context, JavascriptFormatter output, TypeDefinition iface) {
            output.Identifier("JSIL.MakeInterface", null);
            output.LPar();
            output.NewLine();

            output.Value(Util.EscapeIdentifier(iface.FullName, EscapingMode.String));
            output.Comma();

            output.Value(iface.IsPublic);
            output.Comma();

            output.OpenBracket();
            output.CommaSeparatedList(
                (from p in iface.GenericParameters select p.Name), ListValueType.Primitive
            );
            output.CloseBracket();
            output.Comma();

            output.OpenBrace();

            bool isFirst = true;
            foreach (var methodGroup in iface.Methods.GroupBy(method => method.Name)) {
                int methodGroupIndex = 0;
                foreach (var m in methodGroup) {
                    var methodInfo = TypeInfoProvider.GetMethod(m);
                    if ((methodInfo != null) && methodInfo.IsIgnored)
                        continue;

                    if (!isFirst) {
                        output.Comma();
                        output.NewLine();
                    }

                    output.Value(Util.EscapeIdentifier(methodGroup.Count() == 1 ? m.Name : m.Name + "$" + methodGroupIndex++));
                    output.Token(": ");
                    output.Identifier("Function");

                    isFirst = false;
                }
            }

            foreach (var p in iface.Properties) {
                var propertyInfo = TypeInfoProvider.GetProperty(p);
                if ((propertyInfo != null) && propertyInfo.IsIgnored)
                    continue;

                if (!isFirst) {
                    output.Comma();
                    output.NewLine();
                }

                output.Value(Util.EscapeIdentifier(p.Name));
                output.Token(": ");
                output.Identifier("Property");

                isFirst = false;
            }

            output.NewLine();
            output.CloseBrace(false);

            output.RPar();
            output.Semicolon();
            output.NewLine();
        }

        protected void TranslateEnum (DecompilerContext context, JavascriptFormatter output, TypeDefinition enm) {
            var typeInfo = TypeInfoProvider.GetTypeInformation(enm);

            output.Identifier("JSIL.MakeEnum", null);
            output.LPar();
            output.NewLine();

            output.Value(Util.EscapeIdentifier(typeInfo.FullName, EscapingMode.String));
            output.Comma();

            output.Value(enm.IsPublic);
            output.Comma();

            output.OpenBrace();

            var typeInformation = TypeInfoProvider.GetTypeInformation(enm);
            if (typeInformation == null)
                throw new InvalidOperationException();

            bool isFirst = true;
            foreach (var em in typeInformation.EnumMembers.Values) {
                if (!isFirst) {
                    output.Comma();
                    output.NewLine();
                }

                output.Identifier(em.Name);
                output.Token(": ");
                output.Value(em.Value);

                isFirst = false;
            }

            output.NewLine();
            output.CloseBrace(false);
            output.Comma();
            output.Value(typeInformation.IsFlagsEnum);
            output.NewLine();

            output.RPar();
            output.Semicolon();
            output.NewLine();
        }

        protected void TranslateDelegate (DecompilerContext context, JavascriptFormatter output, TypeDefinition del, TypeInfo typeInfo) {
            output.Identifier("JSIL.MakeDelegate", null);
            output.LPar();

            output.Value(Util.EscapeIdentifier(del.FullName, EscapingMode.String));
            output.Comma();

            output.Value(del.IsPublic);

            output.RPar();
            output.Semicolon();
            output.NewLine();
        }

        protected void DeclareType (DecompilerContext context, TypeDefinition typedef, JavascriptAstEmitter astEmitter, JavascriptFormatter output, HashSet<TypeDefinition> declaredTypes, bool stubbed) {
            var typeInfo = TypeInfoProvider.GetTypeInformation(typedef);
            if ((typeInfo == null) || typeInfo.IsIgnored || typeInfo.IsProxy)
                return;

            if (declaredTypes.Contains(typedef))
                return;

            context.CurrentType = typedef;

            declaredTypes.Add(typedef);

            // type has a JS replacement, we can't correctly emit a stub or definition for it.
            // We do want to process nested types and the type definition, though.
            if (typeInfo.Replacement != null) {
                TranslateTypeDefinition(
                    context, typedef, astEmitter, output, stubbed,
                    (o) => o.Token(typeInfo.Replacement)
                );

                output.NewLine();

                foreach (var nestedTypeDef in typedef.NestedTypes)
                    DeclareType(context, nestedTypeDef, astEmitter, output, declaredTypes, stubbed);

                return;
            }

            output.DeclareNamespace(typedef.Namespace);

            if (typeInfo.IsExternal) {
                output.Identifier("JSIL.MakeExternalType", null);
                output.LPar();

                output.Value(typeInfo.FullName);
                output.Comma();
                output.Value(typedef.IsPublic);

                output.RPar();
                output.Semicolon();
                output.NewLine();
                return;
            }

            if (typedef.IsInterface) {
                TranslateInterface(context, output, typedef);
                return;
            } else if (typedef.IsEnum) {
                TranslateEnum(context, output, typedef);
                return;
            } else if (typeInfo.IsDelegate) {
                TranslateDelegate(context, output, typedef, typeInfo);
                return;
            } else if (typeInfo.FullName == "System.Delegate") {
                output.Identifier(output.PrivateToken);
                output.Dot();
                output.Identifier(typeInfo.FullName, EscapingMode.TypeIdentifier);
                output.Space();
                output.Token("=");
                output.Space();
                output.Identifier(typeInfo.FullName, EscapingMode.TypeIdentifier);
                output.Semicolon();
                output.NewLine();
                return;
            }

            var declaringType = typedef.DeclaringType;
            if (declaringType != null)
                DeclareType(context, declaringType, astEmitter, output, declaredTypes, IsStubbed(declaringType.Module.Assembly));

            var baseClass = typedef.BaseType;
            if (baseClass != null) {
                var resolved = baseClass.Resolve();
                if (
                    (resolved != null) &&
                    (resolved.Module.Assembly == typedef.Module.Assembly)
                ) {
                    DeclareType(context, resolved, astEmitter, output, declaredTypes, IsStubbed(resolved.Module.Assembly));
                }
            }

            bool isStatic = typedef.IsAbstract && typedef.IsSealed;

            if (isStatic) {
                output.Identifier("JSIL.MakeStaticClass", null);
                output.LPar();

                output.Value(typeInfo.FullName);
                output.Comma();
                output.Value(typedef.IsPublic);

                output.Comma();
                output.OpenBracket();
                if (typedef.HasGenericParameters)
                    output.CommaSeparatedList(
                        (from p in typedef.GenericParameters select p.Name), ListValueType.Primitive
                    );
                output.CloseBracket();

            } else {
                if (typedef.IsValueType)
                    output.Identifier("JSIL.MakeStruct", null);
                else
                    output.Identifier("JSIL.MakeClass", null);

                output.LPar();

                if (baseClass == null) {
                    if (typedef.FullName != "System.Object")
                        throw new InvalidDataException("Type without base class that isn't System.Object.");

                    output.Identifier("$jsilcore");
                    output.Dot();
                    output.Identifier("System");
                    output.Dot();
                    output.Identifier("Object");
                } else if (typedef.FullName == "System.ValueType") {
                    output.Identifier("$jsilcore");
                    output.Dot();
                    output.Identifier("System");
                    output.Dot();
                    output.Identifier("ValueType");
                } else {
                    output.TypeReference(baseClass);
                }

                output.Comma();

                output.Value(typeInfo.FullName);
                output.Comma();
                output.Value(typedef.IsPublic);

                output.Comma();
                output.OpenBracket();
                if (typedef.HasGenericParameters)
                    output.CommaSeparatedList(
                        (from p in typedef.GenericParameters select p.Name), ListValueType.Primitive
                    );
                output.CloseBracket();

            }

            output.Comma();
            output.OpenFunction(null, (f) => {
                f.Identifier("$");
            });

            TranslateTypeDefinition(
                context, typedef, astEmitter, output, stubbed,
                (o) => o.Identifier("$", null)
            );

            output.NewLine();

            output.CloseBrace(false);

            output.RPar();
            output.Semicolon();
            output.NewLine();

            foreach (var nestedTypeDef in typedef.NestedTypes)
                DeclareType(context, nestedTypeDef, astEmitter, output, declaredTypes, stubbed);
        }

        protected bool ShouldTranslateMethods (TypeDefinition typedef) {
            var typeInfo = TypeInfoProvider.GetTypeInformation(typedef);
            if ((typeInfo == null) || typeInfo.IsIgnored || typeInfo.IsProxy || typeInfo.IsExternal)
                return false;

            if (typedef.IsInterface)
                return false;
            else if (typedef.IsEnum)
                return false;
            else if (typeInfo.IsDelegate)
                return false;

            return true;
        }

        protected void TranslatePrimitiveDefinition (
            DecompilerContext context, JavascriptFormatter output,
            TypeDefinition typedef, bool stubbed, Action<JavascriptFormatter> dollar
        ) {
            bool isIntegral = false;
            bool isNumeric = false;

            switch (typedef.FullName) {
                case "System.Boolean":
                    isIntegral = true;
                    isNumeric = true;
                    break;
                case "System.Char":
                    isIntegral = true;
                    isNumeric = true;
                    break;
                case "System.Byte":
                case "System.SByte":
                case "System.UInt16":
                case "System.Int16":
                case "System.UInt32":
                case "System.Int32":
                case "System.UInt64":
                case "System.Int64":
                    isIntegral = true;
                    isNumeric = true;
                    break;
                case "System.Single":
                case "System.Double":
                case "System.Decimal":
                    isIntegral = false;
                    isNumeric = true;
                    break;
            }

            dollar(output);
            output.Dot();
            output.Identifier("__IsIntegral__");
            output.Token(" = ");
            output.Value(isIntegral);
            output.Semicolon(true);

            dollar(output);
            output.Dot();
            output.Keyword("prototype");
            output.Dot();
            output.Identifier("__IsIntegral__");
            output.Token(" = ");
            output.Value(isIntegral);
            output.Semicolon(true);

            dollar(output);
            output.Dot();
            output.Identifier("__IsNumeric__");
            output.Token(" = ");
            output.Value(isNumeric);
            output.Semicolon(true);

            dollar(output);
            output.Dot();
            output.Keyword("prototype");
            output.Dot();
            output.Identifier("__IsNumeric__");
            output.Token(" = ");
            output.Value(isNumeric);
            output.Semicolon(true);
        }

        protected void TranslateTypeDefinition (
            DecompilerContext context, TypeDefinition typedef, 
            JavascriptAstEmitter astEmitter, JavascriptFormatter output, 
            bool stubbed, Action<JavascriptFormatter> dollar
        ) {
            var typeInfo = TypeInfoProvider.GetTypeInformation(typedef);
            if (!ShouldTranslateMethods(typedef))
                return;

            context.CurrentType = typedef;

            var externalMemberNames = new HashSet<string>();
            var staticExternalMemberNames = new HashSet<string>();

            foreach (var method in typedef.Methods) {
                // We translate the static constructor explicitly later, and inject field initialization
                if (method.Name == ".cctor")
                    continue;

                TranslateMethod(
                    context, method, method, astEmitter, output, 
                    stubbed, externalMemberNames, staticExternalMemberNames, dollar
                );
            }

            if (typedef.IsPrimitive)
                TranslatePrimitiveDefinition(context, output, typedef, stubbed, dollar);

            Action initializeOverloadsAndProperties = () => {
                foreach (var methodGroup in typeInfo.MethodGroups)
                    TranslateMethodGroup(context, output, methodGroup, dollar);

                foreach (var property in typedef.Properties)
                    TranslateProperty(context, output, property, dollar);
            };

            Func<TypeReference, bool> isInterfaceIgnored = (i) => {
                var interfaceInfo = TypeInfoProvider.GetTypeInformation(i);
                if (interfaceInfo != null)
                    return interfaceInfo.IsIgnored;
                else
                    return true;
            };

            Func<FieldDefinition, bool> isFieldIgnored = (f) => {
                IMemberInfo memberInfo;
                if (typeInfo.Members.TryGetValue(MemberIdentifier.New(f), out memberInfo))
                    return memberInfo.IsIgnored;
                else
                    return true;
            };

            var structFields = 
                (from field in typedef.Fields
                where !isFieldIgnored(field) && 
                    !field.HasConstant &&
                    EmulateStructAssignment.IsStruct(field.FieldType) &&
                    !field.IsStatic
                select field).ToArray();

            if (structFields.Length > 0) {
                dollar(output);
                output.Dot();
                output.Identifier("prototype");
                output.Dot();
                output.Identifier("__StructFields__");
                output.Token(" = ");
                output.OpenBracket(true);

                bool isFirst = true;
                foreach (var sf in structFields) {
                    if (!isFirst) {
                        output.Comma();
                        output.NewLine();
                    }

                    output.OpenBracket();
                    output.Value(TypeInfo.GetOriginalName(sf.Name) ?? sf.Name);
                    output.Comma();
                    output.TypeReference(sf.FieldType);
                    output.CloseBracket();

                    isFirst = false;
                }

                output.CloseBracket(true, output.Semicolon);
            }

            TranslateTypeStaticConstructor(context, typedef, astEmitter, output, typeInfo.StaticConstructor, stubbed, dollar);

            if (typedef.FullName == "System.Array")
                staticExternalMemberNames.Add("Of");

            if (externalMemberNames.Count > 0) {
                output.Identifier("JSIL.ExternalMembers", null);
                output.LPar();
                dollar(output);
                output.Comma();
                output.Value(true);
                output.Comma();
                output.NewLine();

                output.CommaSeparatedList(externalMemberNames.OrderBy((n) => n), ListValueType.Primitive);
                output.NewLine();

                output.RPar();
                output.Semicolon();
            }

            if (staticExternalMemberNames.Count > 0) {
                output.Identifier("JSIL.ExternalMembers", null);
                output.LPar();
                dollar(output);
                output.Comma();
                output.Value(false);
                output.Comma();
                output.NewLine();

                output.CommaSeparatedList(staticExternalMemberNames.OrderBy((n) => n), ListValueType.Primitive);
                output.NewLine();

                output.RPar();
                output.Semicolon();
            }

            if ((typeInfo.MethodGroups.Count + typedef.Properties.Count) > 0)
                initializeOverloadsAndProperties();

            var interfaces = (from i in typeInfo.Interfaces
                              where !i.IsIgnored
                              select i).ToArray();

            if (interfaces.Length > 0) {
                output.Identifier("JSIL.ImplementInterfaces", null);
                output.LPar();
                dollar(output);
                output.Comma();
                output.OpenBracket(true);
                output.CommaSeparatedList(interfaces, ListValueType.TypeReference);
                output.CloseBracket(true, () => {
                    output.RPar();
                    output.Semicolon();
                });
            }

            if (typedef.HasCustomAttributes) {
                output.Identifier("JSIL.RegisterCustomAttributes", null);
                output.LPar();
                dollar(output);
                output.Comma();
                output.OpenBracket(true);
                bool isFirst = true;
                foreach (var customAttribute in typedef.CustomAttributes)
                {
                    if (!isFirst)
                        output.Comma();
                    isFirst = false;
                    output.OpenBracket(true);
                    output.TypeReference(customAttribute.AttributeType);
                    output.Comma();
                    output.OpenBracket(true);
                    output.CommaSeparatedList(customAttribute.ConstructorArguments.Select(x => x.Value));
                    output.CloseBracket(true);
                    output.CloseBracket(true);
                }
                output.CloseBracket(true, () =>
                {
                    output.RPar();
                    output.Semicolon();
                });
            }
        }

        protected void TranslateMethodGroup (
            DecompilerContext context, JavascriptFormatter output,
            MethodGroupInfo methodGroup, Action<JavascriptFormatter> dollar
        ) {
            var methods = (from m in methodGroup.Methods where !m.IsIgnored select m).ToArray();
            if (methods.Length < 1)
                return;

            output.Identifier(
                (methods.First().IsGeneric) ? "JSIL.OverloadedGenericMethod" : "JSIL.OverloadedMethod", null
            );
            output.LPar();

            dollar(output);
            if (!methodGroup.IsStatic) {
                output.Dot();
                output.Keyword("prototype");
            }

            output.Comma();
            output.Value(Util.EscapeIdentifier(methodGroup.Name));
            output.Comma();
            output.OpenBracket(true);

            bool isFirst = true;
            foreach (var method in methods) {
                if (!isFirst) {
                    output.Comma();
                    output.NewLine();
                }

                output.OpenBracket();
                output.Value(method.OverloadIndex.Value);
                output.Comma();

                output.OpenBracket();
                output.CommaSeparatedList(
                    from p in method.Member.Parameters select p.ParameterType, 
                    ListValueType.TypeReference
                );
                output.CloseBracket();

                output.CloseBracket();
                isFirst = false;
            }

            output.CloseBracket(true, () => {
                output.Comma();
                output.Identifier(output.PrivateToken);
                output.RPar();
                output.Semicolon();
            });
        }

        internal JSFunctionExpression TranslateMethodExpression (DecompilerContext context, MethodReference method, MethodDefinition methodDef) {
            var oldMethod = context.CurrentMethod;
            try {
                if (method == null)
                    throw new ArgumentNullException("method");
                if (methodDef == null)
                    throw new ArgumentNullException("methodDef");

                var methodInfo = TypeInfoProvider.GetMemberInformation<JSIL.Internal.MethodInfo>(methodDef);

                var identifier = new QualifiedMemberIdentifier(
                    methodInfo.DeclaringType.Identifier, methodInfo.Identifier
                );
                JSFunctionExpression function = null;

                if (FunctionCache.TryGetExpression(identifier, out function))
                    return function;

                if (methodInfo.IsExternal) {
                    FunctionCache.CreateNull(methodInfo, method, identifier);
                    return null;
                }

                var bodyDef = methodDef;
                if (methodInfo.IsFromProxy && methodInfo.Member.HasBody)
                    bodyDef = methodInfo.Member;

                var pr = new ProgressReporter();

                context.CurrentMethod = methodDef;
                if ((methodDef.Body.CodeSize > LargeMethodThreshold) && (this.DecompilingMethod != null))
                    this.DecompilingMethod(method.FullName, pr);

                ILBlock ilb;
                var decompiler = new ILAstBuilder();
                var optimizer = new ILAstOptimizer();

                try {
                    ilb = new ILBlock(decompiler.Build(bodyDef, true, context));
                    optimizer.Optimize(context, ilb);
                } catch (Exception exception) {
                    if (CouldNotDecompileMethod != null)
                        CouldNotDecompileMethod(bodyDef.FullName, exception);

                    FunctionCache.CreateNull(methodInfo, method, identifier);
                    pr.OnFinished();
                    return null;
                }

                var allVariables = ilb.GetSelfAndChildrenRecursive<ILExpression>().Select(e => e.Operand as ILVariable)
                    .Where(v => v != null && !v.IsParameter).Distinct();

                foreach (var v in allVariables) {
                    if (ILBlockTranslator.IsIgnoredType(v.Type)) {
                        FunctionCache.CreateNull(methodInfo, method, identifier);
                        pr.OnFinished();
                        return null;
                    }
                }

                NameVariables.AssignNamesToVariables(context, decompiler.Parameters, allVariables, ilb);

                var translator = new ILBlockTranslator(
                    this, context, method, methodDef, 
                    ilb, decompiler.Parameters, allVariables
                );
                var body = translator.Translate();

                if (body == null) {
                    FunctionCache.CreateNull(methodInfo, method, identifier);
                    pr.OnFinished();
                    return null;
                }

                var parameters = from p in translator.ParameterNames select translator.Variables[p];

                if (method.HasGenericParameters) {
                    var type = new TypeReference("System", "Type", context.CurrentModule.TypeSystem.Object.Module, context.CurrentModule.TypeSystem.Object.Scope);
                    parameters = (from gp in method.GenericParameters select new JSVariable(gp.Name, type, method)).Concat(parameters);
                }

                function = FunctionCache.Create(
                    methodInfo, methodDef, method, identifier, 
                    translator, parameters, body
                );

                pr.OnFinished();
                return function;
            } finally {
                context.CurrentMethod = oldMethod;
            }
        }

        private void OptimizeFunction (
            SpecialIdentifiers si, HashSet<string> parameterNames,
            Dictionary<string, JSVariable> variables, JSFunctionExpression function
        ) {
            Action temporaryEliminationPass = () => {
                if (Configuration.Optimizer.EliminateTemporaries.GetValueOrDefault(true)) {
                    bool eliminated;
                    do {
                        var visitor = new EliminateSingleUseTemporaries(
                            si.TypeSystem, variables, FunctionCache
                        );
                        visitor.Visit(function);
                        eliminated = visitor.EliminatedVariables.Count > 0;
                    } while (eliminated);
                }
            };

            var la = new LabelAnalyzer();
            la.Visit(function);
            la.BuildLabelGroups();

            temporaryEliminationPass();

            new EmulateStructAssignment(
                si.TypeSystem,
                FunctionCache,
                si.CLR,
                Configuration.Optimizer.EliminateStructCopies.GetValueOrDefault(true)
            ).Visit(function);

            new IntroduceVariableDeclarations(
                variables,
                TypeInfoProvider
            ).Visit(function);

            new IntroduceVariableReferences(
                si.JSIL,
                variables,
                parameterNames
            ).Visit(function);

            if (Configuration.Optimizer.SimplifyLoops.GetValueOrDefault(true))
                new SimplifyLoops(
                    si.TypeSystem, false
                ).Visit(function);

            // Temporary elimination makes it possible to simplify more operators, so do it later
            if (Configuration.Optimizer.SimplifyOperators.GetValueOrDefault(true))
                new SimplifyOperators(
                    si.JSIL, si.JS, si.TypeSystem
                ).Visit(function);

            new ReplaceMethodCalls(
                si.JSIL, si.JS, si.TypeSystem
            ).Visit(function);

            new IntroduceCharCasts(
                si.TypeSystem, si.JS
            ).Visit(function);

            new IntroduceEnumCasts(
                si.TypeSystem, TypeInfoProvider
            ).Visit(function);

            var dss = new DeoptimizeSwitchStatements(
                si.TypeSystem
            );
            dss.Visit(function);

            new CollapseNulls().Visit(function);

            if (Configuration.Optimizer.SimplifyLoops.GetValueOrDefault(true))
                new SimplifyLoops(
                    si.TypeSystem, true
                ).Visit(function);

            new ExpandCastExpressions(
                si.TypeSystem, si.JS, si.JSIL
            ).Visit(function);

            temporaryEliminationPass();

            var lnd = new LoopNameDetector();
            lnd.Visit(function);
            lnd.EliminateUnusedLoopNames();
        }

        protected static bool NeedsStaticConstructor (TypeReference type) {
            if (EmulateStructAssignment.IsStruct(type))
                return true;
            else if (type.MetadataType != MetadataType.ValueType)
                return false;

            var resolved = type.Resolve();
            if (resolved == null)
                return true;

            if (resolved.IsEnum)
                return false;

            return true;
        }

        protected JSExpression TranslateField (
            FieldDefinition field, Action<JavascriptFormatter> dollar
        ) {
            JSDotExpression target;
            var fieldInfo = TypeInfoProvider.GetMemberInformation<Internal.FieldInfo>(field);
            if ((fieldInfo == null) || fieldInfo.IsIgnored || fieldInfo.IsExternal)
                return null;
            
            if (field.IsStatic)
                target = JSDotExpression.New(
                    new JSRawOutputIdentifier(dollar, field.DeclaringType), new JSField(field, fieldInfo)
                );
            else
                target = JSDotExpression.New(
                    new JSRawOutputIdentifier(dollar, field.DeclaringType), new JSStringIdentifier("prototype"), new JSField(field, fieldInfo)
                );

            if (field.HasConstant) {
                JSLiteral constant;
                if (field.Constant == null) {
                    constant = JSLiteral.Null(field.FieldType);
                } else {
                    constant = JSLiteral.New(field.Constant as dynamic);
                }

                return JSInvocationExpression.InvokeStatic(
                    JSDotExpression.New(
                        new JSStringIdentifier("JSIL"), new JSFakeMethod("MakeConstant", field.Module.TypeSystem.Void)
                    ), new[] { 
                        target.Target, target.Member.ToLiteral(), constant                            
                    }
                );
            } else {
                return new JSBinaryOperatorExpression(
                    JSOperator.Assignment, target,
                    new JSDefaultValueLiteral(field.FieldType), 
                    field.FieldType
                );
            }
        }

        protected void TranslateTypeStaticConstructor (
            DecompilerContext context, TypeDefinition typedef, 
            JavascriptAstEmitter astEmitter, JavascriptFormatter output, 
            MethodDefinition cctor, bool stubbed, Action<JavascriptFormatter> dollar
        ) {
            var typeSystem = context.CurrentModule.TypeSystem;
            var staticFields = 
                (from f in typedef.Fields
                 where f.IsStatic
                 select f).ToArray();
            var fieldsToEmit =
                (from f in staticFields
                 where NeedsStaticConstructor(f.FieldType)
                 let fi = TypeInfoProvider.GetField(f)
                 where ((fi != null) && (!fi.IsExternal && !fi.IsIgnored)) || (fi == null)
                 select f).ToArray();
            var fieldsToStrip =
                new HashSet<FieldDefinition>(from f in staticFields
                 let fi = TypeInfoProvider.GetField(f)
                 where (fi != null) && (fi.IsExternal || fi.IsIgnored)
                 select f);

            // We initialize all static fields in the cctor to avoid ordering issues
            Action<JSFunctionExpression> fixupCctor = (f) => {
                int insertPosition = 0;

                // Strip initializations of ignored and external fields from the cctor, since
                //  they are generated by the compiler
                var statements = f.Body.Children.OfType<JSExpressionStatement>().ToArray();
                foreach (var es in statements) {
                    var boe = es.Expression as JSBinaryOperatorExpression;
                    if (boe == null)
                        continue;

                    var theDot = boe.Left as JSDotExpression;
                    if (theDot == null)
                        continue;

                    var theField = boe.Left.AllChildrenRecursive.OfType<JSField>().FirstOrDefault();
                    if (theField == null)
                        continue;

                    if (!fieldsToStrip.Contains(theField.Field.Member))
                        continue;

                    // We simply strip the initialization, which leaves the field undefined at runtime.
                    // TODO: It might be be better to generate an external method style placeholder here.
                    f.Body.Statements.Remove(es);
                }

                // Generate field initializations that were not generated by the compiler
                foreach (var field in fieldsToEmit) {
                    var expr = TranslateField(field, dollar);

                    if (expr != null) {
                        var stmt = new JSExpressionStatement(expr);
                        f.Body.Statements.Insert(insertPosition++, stmt);
                    }
                }
            };

            // Default values for instance fields of struct types are handled
            //  by the instance constructor.
            // Default values for static fields of struct types are handled
            //  by the cctor.
            // Everything else is emitted inline.

            foreach (var f in typedef.Fields) {
                if (f.IsStatic && NeedsStaticConstructor(f.FieldType))
                    continue;

                if (EmulateStructAssignment.IsStruct(f.FieldType))
                    continue;

                var fi = TypeInfoProvider.GetField(f);
                if ((fi != null) && (fi.IsIgnored || fi.IsExternal))
                    continue;

                var expr = TranslateField(f, dollar);
                if (expr != null)
                    astEmitter.Visit(new JSExpressionStatement(expr));
            }

            if ((cctor != null) && !stubbed) {
                TranslateMethod(context, cctor, cctor, astEmitter, output, false, null, null, dollar, fixupCctor);
            } else if (fieldsToEmit.Length > 0) {
                var fakeCctor = new MethodDefinition(".cctor", Mono.Cecil.MethodAttributes.Static, typeSystem.Void);
                fakeCctor.DeclaringType = typedef;

                var typeInfo = TypeInfoProvider.GetTypeInformation(typedef);
                typeInfo.StaticConstructor = fakeCctor;
                var identifier = MemberIdentifier.New(fakeCctor);

                typeInfo.Members[identifier] = new Internal.MethodInfo(
                    typeInfo, identifier, fakeCctor, new ProxyInfo[0], false
                );

                // Generate the fake constructor, since it wasn't created during the analysis pass
                TranslateMethodExpression(context, fakeCctor, fakeCctor);

                TranslateMethod(context, fakeCctor, fakeCctor, astEmitter, output, false, null, null, dollar, fixupCctor);
            }
        }

        protected void TranslateMethod (
            DecompilerContext context, MethodReference methodRef, MethodDefinition method,
            JavascriptAstEmitter astEmitter, JavascriptFormatter output, bool stubbed, 
            HashSet<string> externalMemberNames,
            HashSet<string> staticExternalMemberNames,
            Action<JavascriptFormatter> dollar,
            Action<JSFunctionExpression> bodyTransformer = null
        ) {
            var methodInfo = TypeInfoProvider.GetMemberInformation<Internal.MethodInfo>(method);
            if (methodInfo == null)
                return;

            bool isReplaced = methodInfo.Metadata.HasAttribute("JSIL.Meta.JSReplacement");
            bool methodIsProxied = (methodInfo.IsFromProxy && methodInfo.Member.HasBody) && 
                !methodInfo.IsExternal && !isReplaced;

            if (methodInfo.IsExternal || (stubbed && !methodIsProxied)) {
                if (isReplaced)
                    return;

                if (externalMemberNames == null)
                    throw new ArgumentNullException("externalMemberNames");
                if (staticExternalMemberNames == null)
                    throw new ArgumentNullException("staticExternalMemberNames");

                var isProperty = methodInfo.DeclaringProperty != null;

                if (isProperty && methodInfo.DeclaringProperty.IsExternal)
                    return;

                if (!isProperty || !methodInfo.Member.IsCompilerGenerated()) {
                    (method.IsStatic ? staticExternalMemberNames : externalMemberNames)
                        .Add(Util.EscapeIdentifier(methodInfo.GetName(true)));

                    return;
                }
            }

            if (methodInfo.IsIgnored)
                return;
            if (!method.HasBody && !method.IsAbstract)
                return;

            if (methodIsProxied) {
                output.Comment("Implementation from {0}", methodInfo.DeclaringType.FullName);
                output.NewLine();
            }

            dollar(output);
            if (!method.IsStatic) {
                output.Dot();
                output.Keyword("prototype");
            }
            output.Dot();

            output.Identifier(methodInfo.GetName(true));

            output.Token(" = ");

            if (method.HasGenericParameters) {
                output.Identifier("JSIL.GenericMethod", null);
                output.LPar();
                output.NewLine();
                output.OpenBracket();

                output.CommaSeparatedList((from p in method.GenericParameters select p.Name), ListValueType.Primitive);

                output.CloseBracket();
                output.Comma();
                output.NewLine();
            }

            JSFunctionExpression function;
            if (method.IsAbstract)
                function = null;
            else
                function = FunctionCache.GetExpression(new QualifiedMemberIdentifier(
                    methodInfo.DeclaringType.Identifier,
                    methodInfo.Identifier
                ));

            if (bodyTransformer != null)
                bodyTransformer(function);

            if (function != null) {
                astEmitter.Visit(function);
            } else {
                output.Identifier("JSIL.UntranslatableFunction", null);
                output.LPar();
                output.Value(method.FullName);
                output.RPar();
            }

            if (method.HasGenericParameters) {
                output.NewLine();
                output.RPar();
            }

            output.Semicolon();
        }

        protected void TranslateProperty (
            DecompilerContext context, JavascriptFormatter output,
            PropertyDefinition property, Action<JavascriptFormatter> dollar
        ) {
            var propertyInfo = TypeInfoProvider.GetMemberInformation<Internal.PropertyInfo>(property);
            if ((propertyInfo == null) || propertyInfo.IsIgnored || propertyInfo.IsExternal)
                return;

            var isStatic = (property.SetMethod ?? property.GetMethod).IsStatic;

            if (property.DeclaringType.HasGenericParameters && isStatic)
                output.Identifier("JSIL.MakeGenericProperty", null);
            else
                output.Identifier("JSIL.MakeProperty", null);

            output.LPar();

            dollar(output);
            if (!isStatic) {
                output.Dot();
                output.Keyword("prototype");
            }
            output.Comma();

            output.Value(Util.EscapeIdentifier(propertyInfo.Name));

            output.Comma();
            output.NewLine();

            if (property.GetMethod != null) {
                dollar(output);
                if (!isStatic) {
                    output.Dot();
                    output.Keyword("prototype");
                }
                output.Dot();
                output.Identifier(property.GetMethod, false);
            } else {
                output.Keyword("null");
            }

            output.Comma();

            if (property.SetMethod != null) {
                dollar(output);
                if (!isStatic) {
                    output.Dot();
                    output.Keyword("prototype");
                }
                output.Dot();
                output.Identifier(property.SetMethod, false);
            } else {
                output.Keyword("null");
            }

            output.RPar();
            output.Semicolon();
        }
    }
}
