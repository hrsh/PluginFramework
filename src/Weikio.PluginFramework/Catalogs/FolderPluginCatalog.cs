﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Weikio.PluginFramework.Abstractions;
using Weikio.PluginFramework.Context;
using Weikio.PluginFramework.TypeFinding;

namespace Weikio.PluginFramework.Catalogs
{
    /// <summary>
    /// Plugin folder for a single folder (including or excluding subfolders). Locates the plugins from the assemblies (by default this means dll-files).
    /// </summary>
    public class FolderPluginCatalog : IPluginCatalog
    {
        private readonly string _folderPath;
        private readonly FolderPluginCatalogOptions _options;
        private readonly List<AssemblyPluginCatalog> _catalogs = new List<AssemblyPluginCatalog>();

        /// <inheritdoc />
        public bool IsInitialized { get; private set; }

        private List<Plugin> Plugins
        {
            get
            {
                return _catalogs.SelectMany(x => x.GetPlugins()).ToList();
            }
        }

        public FolderPluginCatalog(string folderPath) : this(folderPath, new FolderPluginCatalogOptions())
        {
        }

        public FolderPluginCatalog(string folderPath, FolderPluginCatalogOptions options) : this(folderPath, null, null, options)
        {
        }

        public FolderPluginCatalog(string folderPath, Action<TypeFinderCriteriaBuilder> configureFinder) : this(folderPath, configureFinder, null, null)
        {
        }

        public FolderPluginCatalog(string folderPath, TypeFinderCriteria finderCriteria) : this(folderPath, finderCriteria, null)
        {
        }

        public FolderPluginCatalog(string folderPath, TypeFinderCriteria finderCriteria, FolderPluginCatalogOptions options) : this(folderPath, null, finderCriteria, options)
        {
        }

        public FolderPluginCatalog(string folderPath, Action<TypeFinderCriteriaBuilder> configureFinder, FolderPluginCatalogOptions options) : this(folderPath, configureFinder, null, options)
        {
        }
        
        public FolderPluginCatalog(string folderPath, Action<TypeFinderCriteriaBuilder> configureFinder, TypeFinderCriteria finderCriteria, FolderPluginCatalogOptions options)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                throw new ArgumentNullException(nameof(folderPath));
            }
            
            _folderPath = folderPath;
            _options = options ?? new FolderPluginCatalogOptions();

            if (configureFinder != null)
            {
                var builder = new TypeFinderCriteriaBuilder();
                configureFinder(builder);

                var criteria = builder.Build();

                _options.TypeFinderCriterias.Add("", criteria);
            }
            
            if (finderCriteria != null)
            {
                _options.TypeFinderCriterias.Add("", finderCriteria);
            }

            if (_options.TypeFinderCriteria != null)
            {
                _options.TypeFinderCriterias.Add("", _options.TypeFinderCriteria);
            }
        }

        /// <inheritdoc />
        public List<Plugin> GetPlugins()
        {
            return Plugins;
        }

        /// <inheritdoc />
        public Plugin Get(string name, Version version)
        {
            foreach (var assemblyPluginCatalog in _catalogs)
            {
                var plugin = assemblyPluginCatalog.Get(name, version);

                if (plugin == null)
                {
                    continue;
                }

                return plugin;
            }

            return null;
        }

        /// <inheritdoc />
        public async Task Initialize()
        {
            var foundFiles = new List<string>();

            foreach (var searchPattern in _options.SearchPatterns)
            {
                var dllFiles = Directory.GetFiles(_folderPath, searchPattern,
                    _options.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

                foundFiles.AddRange(dllFiles);
            }

            foundFiles = foundFiles.Distinct().ToList();

            foreach (var assemblyPath in foundFiles)
            {
                // Assemblies are treated as readonly as long as possible
                var isPluginAssembly = IsPluginAssembly(assemblyPath);

                if (isPluginAssembly == false)
                {
                    continue;
                }

                var assemblyCatalogOptions = new AssemblyPluginCatalogOptions
                {
                    PluginLoadContextOptions = _options.PluginLoadContextOptions,
                    TypeFinderCriterias = _options.TypeFinderCriterias,
                    PluginNameOptions = _options.PluginNameOptions
                };

                // We are actually just delegating the responsibility from FolderPluginCatalog to AssemblyPluginCatalog. 
                var assemblyCatalog = new AssemblyPluginCatalog(assemblyPath, assemblyCatalogOptions);
                await assemblyCatalog.Initialize();

                _catalogs.Add(assemblyCatalog);
            }

            IsInitialized = true;
        }

        private bool IsPluginAssembly(string assemblyPath)
        {
            using (Stream stream = File.OpenRead(assemblyPath))
            using (var reader = new PEReader(stream))
            {
                if (!reader.HasMetadata)
                {
                    return false;
                }

                if (_options.TypeFinderCriterias?.Any() != true)
                {
                    // If there are no resolvers, assume that each DLL is a plugin
                    return true;
                }

                var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
                var runtimeAssemblies = Directory.GetFiles(runtimeDirectory, "*.dll");
                var paths = new List<string>(runtimeAssemblies) { assemblyPath };

                if (_options.PluginLoadContextOptions.AdditionalRuntimePaths?.Any() == true)
                {
                    foreach (var additionalRuntimePath in _options.PluginLoadContextOptions.AdditionalRuntimePaths)
                    {
                        var dlls = Directory.GetFiles(additionalRuntimePath, "*.dll");
                        paths.AddRange(dlls);
                    }
                }

                if (_options.PluginLoadContextOptions.UseHostApplicationAssemblies == UseHostApplicationAssembliesEnum.Always)
                {
                    var hostApplicationPath = Environment.CurrentDirectory;
                    var hostDlls = Directory.GetFiles(hostApplicationPath, "*.dll", SearchOption.AllDirectories);

                    paths.AddRange(hostDlls);
                }
                else if (_options.PluginLoadContextOptions.UseHostApplicationAssemblies == UseHostApplicationAssembliesEnum.Never)
                {
                    var pluginPath = Path.GetDirectoryName(assemblyPath);
                    var dllsInPluginPath = Directory.GetFiles(pluginPath, "*.dll", SearchOption.AllDirectories);

                    paths.AddRange(dllsInPluginPath);
                }
                else if (_options.PluginLoadContextOptions.UseHostApplicationAssemblies == UseHostApplicationAssembliesEnum.Selected)
                {
                    foreach (var hostApplicationAssembly in _options.PluginLoadContextOptions.HostApplicationAssemblies)
                    {
                        var assembly = Assembly.Load(hostApplicationAssembly);
                        paths.Add(assembly.Location);
                    }
                }

                var resolver = new PathAssemblyResolver(paths);

                // We use the metadata (readonly) versions of the assemblies before loading them
                using (var metadataContext = new MetadataLoadContext(resolver))
                {
                    var metadataPluginLoadContext = new MetadataTypeFindingContext(metadataContext);
                    var readonlyAssembly = metadataContext.LoadFromAssemblyPath(assemblyPath);

                    var typeFinder = new TypeFinder();

                    foreach (var finderCriteria in _options.TypeFinderCriterias)
                    {
                        var typesFound = typeFinder.Find(finderCriteria.Value, readonlyAssembly, metadataPluginLoadContext);

                        if (typesFound?.Any() == true)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
