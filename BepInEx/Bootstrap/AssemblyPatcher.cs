﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Common;
using Mono.Cecil;

namespace BepInEx.Bootstrap
{
	/// <summary>
	/// Delegate used in patching assemblies.
	/// </summary>
	/// <param name="assembly">The assembly that is being patched.</param>
    public delegate void AssemblyPatcherDelegate(ref AssemblyDefinition assembly);

	/// <summary>
	/// Worker class which is used for loading and patching entire folders of assemblies, or alternatively patching and loading assemblies one at a time.
	/// </summary>
    public static class AssemblyPatcher
    {
		/// <summary>
		/// Configuration value of whether assembly dumping is enabled or not.
		/// </summary>
        private static bool DumpingEnabled => bool.TryParse(Config.GetEntry("preloader-dumpassemblies", "false"), out bool result) ? result : false;

        /// <summary>
        /// Patches and loads an entire directory of assemblies.
        /// </summary>
        /// <param name="directory">The directory to load assemblies from.</param>
        /// <param name="patcherMethodDictionary">The dictionary of patchers and their targeted assembly filenames which they are patching.</param>
        /// <param name="initializers">List of initializers to run before any patching starts</param>
        /// <param name="finalizers">List of finalizers to run before returning</param>
        public static void PatchAll(string directory, IDictionary<AssemblyPatcherDelegate, IEnumerable<string>> patcherMethodDictionary, IEnumerable<Action> initializers = null, IEnumerable<Action> finalizers = null)
        {
			//run all initializers
			if (initializers != null)
				foreach (Action init in initializers)
					init.Invoke();

            //load all the requested assemblies
            List<AssemblyDefinition> assemblies = new List<AssemblyDefinition>();
            Dictionary<AssemblyDefinition, string> assemblyFilenames = new Dictionary<AssemblyDefinition, string>();

            foreach (string assemblyPath in Directory.GetFiles(directory, "*.dll"))
            {
                var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
                
                //NOTE: this is special cased here because the dependency handling for System.dll is a bit wonky
                //System has an assembly reference to itself, and it also has a reference to Mono.Security causing a circular dependency
                //It's also generally dangerous to change system.dll since so many things rely on it, 
                // and it's already loaded into the appdomain since this loader references it, so we might as well skip it
                if (assembly.Name.Name == "System"
                    || assembly.Name.Name == "mscorlib") //mscorlib is already loaded into the appdomain so it can't be patched
                {
#if CECIL_10
                    assembly.Dispose();
#endif
                    continue;
                }

                assemblies.Add(assembly);
                assemblyFilenames[assembly] = Path.GetFileName(assemblyPath);
            }

            //generate a dictionary of each assembly's dependencies
            Dictionary<AssemblyDefinition, IList<AssemblyDefinition>> assemblyDependencyDict = new Dictionary<AssemblyDefinition, IList<AssemblyDefinition>>();
            
            foreach (AssemblyDefinition assembly in assemblies)
            {
                assemblyDependencyDict[assembly] = new List<AssemblyDefinition>();

                foreach (var dependencyRef in assembly.MainModule.AssemblyReferences)
                {
                    var dependencyAssembly = assemblies.FirstOrDefault(x => x.FullName == dependencyRef.FullName);

                    if (dependencyAssembly != null)
                        assemblyDependencyDict[assembly].Add(dependencyAssembly);
                }
            }

            //sort the assemblies so load the assemblies that are dependant upon first
            AssemblyDefinition[] sortedAssemblies = Utility.TopologicalSort(assemblies, x => assemblyDependencyDict[x]).ToArray();

            Dictionary<string, AssemblyDefinition> sortedAssemblyFilenames = sortedAssemblies.ToDictionary(ass => assemblyFilenames[ass]);

            HashSet<string> patchedAssemblies = new HashSet<string>();

            //call the patchers on the assemblies
	        foreach (var patcherMethod in patcherMethodDictionary)
	        {
		        foreach (string assemblyFilename in patcherMethod.Value)
		        {
		            if (sortedAssemblyFilenames.TryGetValue(assemblyFilename, out var assembly))
		            {
		                Patch(ref assembly, patcherMethod.Key);
		                patchedAssemblies.Add(assemblyFilenames[assembly]);
                    }
		        }
	        }

            // Finally, load all assemblies into memory
			foreach (var assembly in sortedAssemblies)
			{
			    string filename = Path.GetFileName(assemblyFilenames[assembly]);

			    if (DumpingEnabled && patchedAssemblies.Contains(filename))
			    {
			        using (MemoryStream mem = new MemoryStream())
			        {
			            string dirPath = Path.Combine(Paths.PluginPath, "DumpedAssemblies");

			            if (!Directory.Exists(dirPath))
			                Directory.CreateDirectory(dirPath);
                            
			            assembly.Write(mem);
			            File.WriteAllBytes(Path.Combine(dirPath, filename), mem.ToArray());
			        }
			    }

			    Load(assembly);
#if CECIL_10
			    assembly.Dispose();
#endif
			}
			
	        //run all finalizers
	        if (finalizers != null)
		        foreach (Action finalizer in finalizers)
			        finalizer.Invoke();
        }

		/// <summary>
		/// Patches an individual assembly, without loading it.
		/// </summary>
		/// <param name="assembly">The assembly definition to apply the patch to.</param>
		/// <param name="patcherMethod">The patcher to use to patch the assembly definition.</param>
        public static void Patch(ref AssemblyDefinition assembly, AssemblyPatcherDelegate patcherMethod)
        {
	        patcherMethod.Invoke(ref assembly);
        }

		/// <summary>
		/// Loads an individual assembly defintion into the CLR.
		/// </summary>
		/// <param name="assembly">The assembly to load.</param>
	    public static void Load(AssemblyDefinition assembly)
	    {
		    using (MemoryStream assemblyStream = new MemoryStream())
		    {
			    assembly.Write(assemblyStream);
			    Assembly.Load(assemblyStream.ToArray());
		    }
	    }
    }
}
