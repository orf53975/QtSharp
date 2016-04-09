﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using CppSharp;
using CppSharp.AST;
using CppSharp.AST.Extensions;
using CppSharp.Generators;
using CppSharp.Passes;
using CppAbi = CppSharp.Parser.AST.CppAbi;

namespace QtSharp
{
    public class QtSharp : ILibrary
    {
        public QtSharp(QtModuleInfo qtModuleInfo)
        {
            this.qmake = qtModuleInfo.Qmake;
            this.includePath = qtModuleInfo.IncludePath.Replace('/', Path.DirectorySeparatorChar);
            this.module = Regex.Match(qtModuleInfo.Library, @"Qt\d?(?<module>\w+?)d?(\.\w+)?$").Groups["module"].Value;
            this.libraryPath = qtModuleInfo.LibraryPath.Replace('/', Path.DirectorySeparatorChar);
            this.library = qtModuleInfo.Library;
            this.target = qtModuleInfo.Target;
            this.systemIncludeDirs = qtModuleInfo.SystemIncludeDirs;
            this.frameworkDirs = qtModuleInfo.FrameworkDirs;
            this.make = qtModuleInfo.Make;
            this.docs = qtModuleInfo.Docs;
        }

        public string LibraryName { get; set; }
        public string InlinesLibraryPath { get; set; }

        public void Preprocess(Driver driver, ASTContext lib)
        {
            var qtModule = "Qt" + this.module;
            string moduleIncludes;
            if (Platform.IsMacOS)
            {
                var framework = string.Format("{0}.framework", this.library);
                moduleIncludes = Path.Combine(this.libraryPath, framework, "Headers");
            }
            else
            {
                moduleIncludes = Path.Combine(this.includePath, qtModule);
            }
            foreach (var unit in lib.TranslationUnits.Where(u => u.FilePath != "<invalid>"))
            {
                if (Path.GetDirectoryName(unit.FilePath) != moduleIncludes)
                {
                    LinkDeclaration(unit);
                }
                else
                {
                    IgnorePrivateDeclarations(unit);
                }
            }
            lib.SetClassAsValueType("QByteArray");
            lib.SetClassAsValueType("QListData");
            lib.SetClassAsValueType("QListData::Data");
            lib.SetClassAsValueType("QLocale");
            lib.SetClassAsValueType("QModelIndex");
            lib.SetClassAsValueType("QPoint");
            lib.SetClassAsValueType("QPointF");
            lib.SetClassAsValueType("QSize");
            lib.SetClassAsValueType("QSizeF");
            lib.SetClassAsValueType("QRect");
            lib.SetClassAsValueType("QRectF");
            lib.SetClassAsValueType("QGenericArgument");
            lib.SetClassAsValueType("QGenericReturnArgument");
            lib.SetClassAsValueType("QVariant");
            lib.IgnoreClassMethodWithName("QString", "fromStdWString");
            lib.IgnoreClassMethodWithName("QString", "toStdWString");
            string[] classesWithTypeEnums = { };
            switch (this.module)
            {
                case "Core":
                    // QString is type-mapped to string so we only need two methods for the conversion
                    var qString = lib.FindCompleteClass("QString");
                    foreach (var @class in qString.Declarations)
                    {
                        @class.ExplicitlyIgnore();
                    }
                    foreach (var method in qString.Methods.Where(m => m.OriginalName != "utf16" && m.OriginalName != "fromUtf16"))
                    {
                        method.ExplicitlyIgnore();
                    }
                    break;
                case "Widgets":
                    classesWithTypeEnums = new[]
                                           {
                                               "QGraphicsEllipseItem", "QGraphicsItemGroup", "QGraphicsLineItem",
                                               "QGraphicsPathItem", "QGraphicsPixmapItem", "QGraphicsPolygonItem",
                                               "QGraphicsProxyWidget", "QGraphicsRectItem", "QGraphicsSimpleTextItem",
                                               "QGraphicsTextItem", "QGraphicsWidget"
                                           };
                    // HACK: work around https://github.com/mono/CppSharp/issues/594
                    lib.FindCompleteClass("QGraphicsItem").FindEnum("Extension").Access = AccessSpecifier.Public;
                    lib.FindCompleteClass("QAbstractSlider").FindEnum("SliderChange").Access = AccessSpecifier.Public;
                    lib.FindCompleteClass("QAbstractItemView").FindEnum("CursorAction").Access = AccessSpecifier.Public;
                    lib.FindCompleteClass("QAbstractItemView").FindEnum("State").Access = AccessSpecifier.Public;
                    lib.FindCompleteClass("QAbstractItemView").FindEnum("DropIndicatorPosition").Access = AccessSpecifier.Public;
                    break;
                case "Svg":
                    classesWithTypeEnums = new[] { "QGraphicsSvgItem" };
                    break;
            }
            foreach (var enumeration in from @class in classesWithTypeEnums
                                        from @enum in lib.FindCompleteClass(@class).Enums
                                        where string.IsNullOrEmpty(@enum.Name)
                                        select @enum)
            {
                enumeration.Name = "TypeEnum";
            }
        }

        private static void LinkDeclaration(Declaration declaration)
        {
            declaration.GenerationKind = GenerationKind.Link;
            DeclarationContext declarationContext = declaration as DeclarationContext;
            if (declarationContext != null)
            {
                foreach (var nestedDeclaration in declarationContext.Declarations)
                {
                    LinkDeclaration(nestedDeclaration);
                }
            }
        }

        private static void IgnorePrivateDeclarations(DeclarationContext unit)
        {
            foreach (var declaration in unit.Declarations)
            {
                IgnorePrivateDeclaration(declaration);
            }
        }

        private static void IgnorePrivateDeclaration(Declaration declaration)
        {
            if (declaration.Name != null &&
                (declaration.Name.StartsWith("Private") || declaration.Name.EndsWith("Private")))
            {
                declaration.ExplicityIgnored = true;
            }
            else
            {
                DeclarationContext declarationContext = declaration as DeclarationContext;
                if (declarationContext != null)
                {
                    IgnorePrivateDeclarations(declarationContext);
                }
            }
        }

        public void Postprocess(Driver driver, ASTContext lib)
        {
            new ClearCommentsPass().VisitLibrary(driver.ASTContext);
            new GetCommentsFromQtDocsPass(this.docs, this.module).VisitLibrary(driver.ASTContext);
            new CaseRenamePass(
                RenameTargets.Function | RenameTargets.Method | RenameTargets.Property | RenameTargets.Delegate |
                RenameTargets.Field | RenameTargets.Variable,
                RenameCasePattern.UpperCamelCase).VisitLibrary(driver.ASTContext);
            switch (this.module)
            {
                case "Core":
                    var qChar = lib.FindCompleteClass("QChar");
                    var op = qChar.FindOperator(CXXOperatorKind.ExplicitConversion)
                        .FirstOrDefault(o => o.Parameters[0].Type.IsPrimitiveType(PrimitiveType.Char));
                    if (op != null)
                        op.ExplicitlyIgnore();
                    op = qChar.FindOperator(CXXOperatorKind.Conversion)
                        .FirstOrDefault(o => o.Parameters[0].Type.IsPrimitiveType(PrimitiveType.Int));
                    if (op != null)
                        op.ExplicitlyIgnore();
                    // QString is type-mapped to string so we only need two methods for the conversion
                    // go through the methods a second time to ignore free operators moved to the class
                    var qString = lib.FindCompleteClass("QString");
                    foreach (var method in qString.Methods.Where(
                        m => !m.Ignore && m.OriginalName != "utf16" && m.OriginalName != "fromUtf16"))
                    {
                        method.ExplicitlyIgnore();
                    }
                    break;
            }
        }

        public void Setup(Driver driver)
        {
            driver.Options.GeneratorKind = GeneratorKind.CSharp;
            var qtModule = "Qt" + this.module;
            driver.Options.MicrosoftMode = false;
            driver.Options.NoBuiltinIncludes = true;
            driver.Options.TargetTriple = this.target;
            driver.Options.Abi = CppAbi.Itanium;
            driver.Options.LibraryName = string.Format("{0}Sharp", qtModule);
            driver.Options.OutputNamespace = qtModule;
            driver.Options.Verbose = true;
            driver.Options.GenerateInterfacesForMultipleInheritance = true;
            driver.Options.GeneratePropertiesAdvanced = true;
            driver.Options.IgnoreParseWarnings = true;
            driver.Options.CheckSymbols = true;
            driver.Options.GenerateSingleCSharpFile = true;
            driver.Options.GenerateInlines = true;
            driver.Options.CompileCode = true;
            driver.Options.GenerateDefaultValuesForArguments = true;
            driver.Options.GenerateConversionOperators = true;
            driver.Options.MarshalCharAsManagedChar = true;
            driver.Options.Headers.Add(qtModule);

            foreach (var systemIncludeDir in this.systemIncludeDirs)
                driver.Options.addSystemIncludeDirs(systemIncludeDir);
            
            if (Platform.IsMacOS)
            {
                foreach (var frameworkDir in this.frameworkDirs)
                    driver.Options.addArguments(string.Format("-F{0}", frameworkDir));
                driver.Options.addArguments(string.Format("-F{0}", libraryPath));

                var framework = string.Format("{0}.framework", this.library);
                driver.Options.addLibraryDirs(Path.Combine(this.libraryPath, framework));
                driver.Options.addIncludeDirs(Path.Combine(this.libraryPath, framework, "Headers"));
            }

            driver.Options.addIncludeDirs(this.includePath);

            var moduleInclude = Path.Combine(this.includePath, qtModule);
            if (Directory.Exists(moduleInclude))
                driver.Options.addIncludeDirs(moduleInclude);
            
            driver.Options.addLibraryDirs(this.libraryPath);
            driver.Options.Libraries.Add(this.library);
            driver.Options.ExplicitlyPatchedVirtualFunctions.Add("qt_metacall");
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            switch (this.module)
            {
                case "Core":
                    driver.Options.CodeFiles.Add(Path.Combine(dir, "QObject.cs"));
                    driver.Options.CodeFiles.Add(Path.Combine(dir, "QChar.cs"));
                    driver.Options.CodeFiles.Add(Path.Combine(dir, "_iobuf.cs"));
                    break;
                case "Gui":
                    // HACK: work around https://github.com/mono/CppSharp/issues/582
                    driver.Options.CodeFiles.Add(Path.Combine(dir, "IQAccessibleActionInterface.cs"));
                    break;
                case "Qml":
                    // HACK: work around https://github.com/mono/CppSharp/issues/582
                    driver.Options.CodeFiles.Add(Path.Combine(dir, "IQQmlParserStatus.cs"));
                    break;
            }
            this.LibraryName = driver.Options.LibraryName + ".dll";
            var prefix = Platform.IsWindows ? string.Empty : "lib";
            var extension = Platform.IsWindows ? ".dll" : Platform.IsMacOS ? ".dylib" : ".so";
            var inlinesLibraryFile = string.Format("{0}{1}{2}", prefix, driver.Options.InlinesLibraryName, extension);
            this.InlinesLibraryPath = Path.Combine(driver.Options.OutputDir, Platform.IsWindows ? "release" : string.Empty, inlinesLibraryFile);
        }

        public void SetupPasses(Driver driver)
        {
            driver.TranslationUnitPasses.AddPass(new CompileInlinesPass(this.qmake, this.make));
            driver.TranslationUnitPasses.AddPass(new GenerateSignalEventsPass());
            driver.TranslationUnitPasses.AddPass(new GenerateEventEventsPass());
        }

        private readonly string qmake;
        private readonly string make;
        private readonly string includePath;
        private readonly string module;
        private readonly string libraryPath;
        private readonly string library;
        private readonly IEnumerable<string> systemIncludeDirs;
        private readonly IEnumerable<string> frameworkDirs;
        private readonly string target;
        private readonly string docs;
    }
}
