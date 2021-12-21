//
// Runtime.cs: Mac/iOS shared runtime code
//
// Authors:
//   Miguel de Icaza
//
// Copyright 2013 Xamarin Inc.

#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Foundation;
using Registrar;

#if MONOMAC
using AppKit;
#endif

namespace ObjCRuntime
{

	public partial class Runtime
	{
#if !COREBUILD
#pragma warning disable 8618 // "Non-nullable field '...' must contain a non-null value when exiting constructor. Consider declaring the field as nullable.": we make sure through other means that these will never be null
		static Dictionary<IntPtrTypeValueTuple, Delegate> block_to_delegate_cache;
		static Dictionary<Type, ConstructorInfo> intptr_ctor_cache;
		static Dictionary<Type, ConstructorInfo> intptr_bool_ctor_cache;

		static List<object> delegates;
		static List<Assembly> assemblies;
		static Dictionary<IntPtr, GCHandle> object_map;
		static object lock_obj;
		static IntPtr NSObjectClass;
		static bool initialized;

		internal static IntPtrEqualityComparer IntPtrEqualityComparer;
		internal static TypeEqualityComparer TypeEqualityComparer;

		internal static DynamicRegistrar Registrar;
#pragma warning restore 8618

		internal const uint INVALID_TOKEN_REF = 0xFFFFFFFF;

		internal unsafe struct MTRegistrationMap
		{
			public IntPtr assembly;
			public MTClassMap* map;
			public IntPtr full_token_references; /* array of MTFullTokenReference */
			public MTManagedClassMap* skipped_map;
			public MTProtocolWrapperMap* protocol_wrapper_map;
			public MTProtocolMap protocol_map;
			public int assembly_count;
			public int map_count;
			public int full_token_reference_count;
			public int skipped_map_count;
			public int protocol_wrapper_count;
			public int protocol_count;
		}

		[Flags]
		internal enum MTTypeFlags : uint
		{
			None = 0,
			CustomType = 1,
			UserType = 2,
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		internal struct MTClassMap
		{
			public IntPtr handle;
			public uint type_reference;
			public MTTypeFlags flags;
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		internal struct MTManagedClassMap
		{
			public uint skipped_reference; // implied token type: TypeDef
			public uint actual_reference; // implied token type: TypeDef
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		internal struct MTProtocolWrapperMap
		{
			public uint protocol_token;
			public uint wrapper_token;
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		internal unsafe struct MTProtocolMap
		{
			public uint* protocol_tokens;
			public IntPtr* protocols;
		}

		/* Keep Delegates, Trampolines and InitializationOptions in sync with monotouch-glue.m */
		internal struct Trampolines
		{
			public IntPtr tramp;
			public IntPtr stret_tramp;
			public IntPtr fpret_single_tramp;
			public IntPtr fpret_double_tramp;
			public IntPtr release_tramp;
			public IntPtr retain_tramp;
			public IntPtr static_tramp;
			public IntPtr ctor_tramp;
			public IntPtr x86_double_abi_stret_tramp;
			public IntPtr static_fpret_single_tramp;
			public IntPtr static_fpret_double_tramp;
			public IntPtr static_stret_tramp;
			public IntPtr x86_double_abi_static_stret_tramp;
			public IntPtr long_tramp;
			public IntPtr static_long_tramp;
#if MONOMAC
			public IntPtr copy_with_zone_1;
			public IntPtr copy_with_zone_2;
#endif
			public IntPtr get_gchandle_tramp;
			public IntPtr set_gchandle_tramp;
			public IntPtr get_flags_tramp;
			public IntPtr set_flags_tramp;
		}

		[Flags]
		internal enum InitializationFlags : int
		{
			IsPartialStaticRegistrar = 0x01,
			/* unused				= 0x02,*/
			DynamicRegistrar = 0x04,
			/* unused				= 0x08,*/
			IsSimulator = 0x10,
#if NET
			IsCoreCLR				= 0x20,
#endif
		}

#if MONOMAC
		/* This enum must always match the identical enum in runtime/xamarin/main.h */
		internal enum LaunchMode : int {
			App = 0,
			Extension = 1,
			Embedded = 2,
		}
#endif

		[StructLayout(LayoutKind.Sequential)]
		internal unsafe struct InitializationOptions
		{
			public int Size;
			public InitializationFlags Flags;
			public Delegates* Delegates;
			public Trampolines* Trampolines;
			public MTRegistrationMap* RegistrationMap;
			public MarshalObjectiveCExceptionMode MarshalObjectiveCExceptionMode;
			public MarshalManagedExceptionMode MarshalManagedExceptionMode;
#if MONOMAC
			public LaunchMode LaunchMode;
			public IntPtr EntryAssemblyPath; /* char * */
#endif
			IntPtr AssemblyLocations;

#if NET
			public IntPtr xamarin_objc_msgsend;
			public IntPtr xamarin_objc_msgsend_super;
			public IntPtr xamarin_objc_msgsend_stret;
			public IntPtr xamarin_objc_msgsend_super_stret;
			public IntPtr unhandled_exception_handler;
			public IntPtr reference_tracking_begin_end_callback;
			public IntPtr reference_tracking_is_referenced_callback;
			public IntPtr reference_tracking_tracked_object_entered_finalization;
#endif
			public bool IsSimulator
			{
				get
				{
					return (Flags & InitializationFlags.IsSimulator) == InitializationFlags.IsSimulator;
				}
			}
		}

		internal static unsafe InitializationOptions* options;

#if NET
		[BindingImpl (BindingImplOptions.Optimizable)]
		internal unsafe static bool IsCoreCLR {
			get {
				// The linker may turn calls to this property into a constant
				return (options->Flags.HasFlag (InitializationFlags.IsCoreCLR));
			}
		}
#endif

		[BindingImpl(BindingImplOptions.Optimizable)]
		public static bool DynamicRegistrationSupported
		{
			get
			{
				// The linker may turn calls to this property into a constant
				return true;
			}
		}

		internal static bool Initialized
		{
			get { return initialized; }
		}

#if MONOMAC
		[DllImport (Constants.libcLibrary)]
		static extern int _NSGetExecutablePath (byte[] buf, ref int bufsize);
#endif

		[Preserve] // called from native - runtime.m.
		[BindingImpl(BindingImplOptions.Optimizable)] // To inline the Runtime.DynamicRegistrationSupported code if possible.
		unsafe static void Initialize(InitializationOptions* options)
		{
#if PROFILE
			var watch = new Stopwatch ();
#endif
			if (options->Size != Marshal.SizeOf(typeof(InitializationOptions)))
			{
				string msg = "Version mismatch between the native " + ProductName + " runtime and " + AssemblyName + ". Please reinstall " + ProductName + ".";
				NSLog(msg);
#if MONOMAC
				try {
					// Print out where Xamarin.Mac.dll and the native runtime was loaded from.
					NSLog (AssemblyName + " was loaded from {0}", typeof (nint).Assembly.Location);

					var sym2 = Dlfcn.dlsym (Dlfcn.RTLD.Default, "xamarin_initialize");
					Dlfcn.Dl_info info2;
					if (Dlfcn.dladdr (sym2, out info2) == 0) {
						NSLog ("The native runtime was loaded from {0}", Marshal.PtrToStringAuto (info2.dli_fname));
					} else if (Dlfcn.dlsym (Dlfcn.RTLD.MainOnly, "xamarin_initialize") != IntPtr.Zero) {
						var buf = new byte [128];
						int length = buf.Length;
						if (_NSGetExecutablePath (buf, ref length) == -1) {
							Array.Resize (ref buf, length);
							length = buf.Length;
							if (_NSGetExecutablePath (buf, ref length) != 0) {
								NSLog ("Could not find out where the native runtime was loaded from.");
								buf = null;
							}
						}
						if (buf is not null) {
							var str_length = 0;
							for (int i = 0; i < buf.Length && buf [i] != 0; i++)
								str_length++;
							NSLog ("The native runtime was loaded from {0}", System.Text.Encoding.UTF8.GetString (buf, 0, str_length));
						}
					} else {
						NSLog ("Could not find out where the native runtime was loaded from.");
					}
				} catch {
					// Just ignore any exceptions, the above code is just a debug help, and if it fails,
					// any exception show to the user will likely confuse more than help
				}
#endif
				throw ErrorHelper.CreateError(8001, msg);
			}

			if (IntPtr.Size != sizeof(nint))
			{
				string msg = string.Format("Native type size mismatch between " + AssemblyName + " and the executing architecture. " + AssemblyName + " was built for {0}-bit, while the current process is {1}-bit.",
					IntPtr.Size == 4 ? "64" : "32", IntPtr.Size == 4 ? "32" : "64");
				NSLog(msg);
				throw ErrorHelper.CreateError(8010, msg);
			}

			IntPtrEqualityComparer = new IntPtrEqualityComparer();
			TypeEqualityComparer = new TypeEqualityComparer();

			Runtime.options = options;
			delegates = new List<object>();
			object_map = new Dictionary<IntPtr, GCHandle>(IntPtrEqualityComparer);
			intptr_ctor_cache = new Dictionary<Type, ConstructorInfo>(TypeEqualityComparer);
			intptr_bool_ctor_cache = new Dictionary<Type, ConstructorInfo>(TypeEqualityComparer);
			lock_obj = new object();

			NSObjectClass = NSObject.Initialize();

			if (DynamicRegistrationSupported)
				Registrar = new DynamicRegistrar();
			RegisterDelegates(options);
			Class.Initialize(options);
#if !NET
			// This is not needed for .NET 5:
			// * https://github.com/xamarin/xamarin-macios/issues/7924#issuecomment-588331822
			// * https://github.com/xamarin/xamarin-macios/issues/7924#issuecomment-589356481
			Mono.SystemDependencyProvider.Initialize();
#endif
			InitializePlatform(options);

#if !XAMMAC_SYSTEM_MONO && !NET
			UseAutoreleasePoolInThreadPool = true;
#endif
			IsARM64CallingConvention = GetIsARM64CallingConvention(); // Can only be done after Runtime.Arch is set (i.e. InitializePlatform has been called).

			objc_exception_mode = options->MarshalObjectiveCExceptionMode;
			managed_exception_mode = options->MarshalManagedExceptionMode;

#if NET
			if (IsCoreCLR)
				InitializeCoreCLRBridge (options);
#endif

			initialized = true;
#if PROFILE
			Console.WriteLine ("Runtime.Initialize completed in {0} ms", watch.ElapsedMilliseconds);
#endif
		}

#if !XAMMAC_SYSTEM_MONO
#if NET
		[EditorBrowsable (EditorBrowsableState.Never)]
		[Obsolete ("Use the 'AutoreleasePoolSupport' MSBuild property instead: https://docs.microsoft.com/en-us/dotnet/core/run-time-config/threading#autoreleasepool-for-managed-threads.")]
		public static bool UseAutoreleasePoolInThreadPool {
			get {
				return AppContext.TryGetSwitch ("System.Threading.Thread.EnableAutoreleasePool", out var enabled) && enabled;
			}
			set {
				throw new PlatformNotSupportedException ("Use the 'AutoreleasePoolSupport' MSBuild property instead: https://docs.microsoft.com/en-us/dotnet/core/run-time-config/threading#autoreleasepool-for-managed-threads");
			}
		}
#else
		static bool has_autoreleasepool_in_thread_pool;
		public static bool UseAutoreleasePoolInThreadPool
		{
			get
			{
				return has_autoreleasepool_in_thread_pool;
			}
			set
			{
				System.Threading._ThreadPoolWaitCallback.SetDispatcher(value ? new Func<Func<bool>, bool>(ThreadPoolDispatcher) : null);
				has_autoreleasepool_in_thread_pool = value;
			}
		}

		static bool ThreadPoolDispatcher(Func<bool> callback)
		{
			using (var pool = new NSAutoreleasePool())
				return callback();
		}
#endif // NET
#endif

#if MONOMAC
		public static event AssemblyRegistrationHandler? AssemblyRegistration;

		static bool OnAssemblyRegistration (AssemblyName assembly_name)
		{
			if (AssemblyRegistration is not null) {
				var args = new AssemblyRegistrationEventArgs
				{
					Register = true,
					AssemblyName = assembly_name
				};
				AssemblyRegistration (null, args);
				return args.Register;
			}
			return true;
		}
#endif
		static MarshalObjectiveCExceptionMode objc_exception_mode;
		static MarshalManagedExceptionMode managed_exception_mode;

		public static event MarshalObjectiveCExceptionHandler? MarshalObjectiveCException;
		public static event MarshalManagedExceptionHandler? MarshalManagedException;

		static MarshalObjectiveCExceptionMode OnMarshalObjectiveCException(IntPtr exception_handle, bool throwManagedAsDefault)
		{
			if (throwManagedAsDefault && MarshalObjectiveCException is null)
				return MarshalObjectiveCExceptionMode.ThrowManagedException;

			if (MarshalObjectiveCException is not null)
			{
				var exception = GetNSObject<NSException>(exception_handle);
				var args = new MarshalObjectiveCExceptionEventArgs()
				{
					Exception = exception,
					ExceptionMode = throwManagedAsDefault ? MarshalObjectiveCExceptionMode.ThrowManagedException : objc_exception_mode,
				};

				MarshalObjectiveCException(null, args);
				return args.ExceptionMode;
			}
			return objc_exception_mode;
		}

		static MarshalManagedExceptionMode OnMarshalManagedException(IntPtr exception_handle)
		{
			if (MarshalManagedException is not null)
			{
				var exception = GCHandle.FromIntPtr(exception_handle).Target as Exception;
				var args = new MarshalManagedExceptionEventArgs()
				{
					Exception = exception,
					ExceptionMode = managed_exception_mode,
				};
				MarshalManagedException(null, args);
				return args.ExceptionMode;
			}
			return managed_exception_mode;
		}

		static IntPtr GetFunctionPointer(Delegate d)
		{
			delegates.Add(d);
			return Marshal.GetFunctionPointerForDelegate(d);
		}

		// value_handle: GCHandle to a (smart) enum value
		// returns: a handle to a native NSString *
		static IntPtr ConvertSmartEnumToNSString(IntPtr value_handle)
		{
			var value = GetGCHandleTarget(value_handle)!;
			var smart_type = value.GetType();
			MethodBase getConstantMethod, getValueMethod;
			if (!Registrar.IsSmartEnum(smart_type, out getConstantMethod, out getValueMethod))
				throw ErrorHelper.CreateError(8024, $"Could not find a valid extension type for the smart enum '{smart_type.FullName}'. Please file a bug at https://github.com/xamarin/xamarin-macios/issues/new.");
			var rv = (NSString?)((MethodInfo)getConstantMethod).Invoke(null, new object[] { value });
			if (rv is null)
				return IntPtr.Zero;
			rv.DangerousRetain().DangerousAutorelease();
			return rv.Handle;
		}


		// value: native NSString *
		// returns: GCHandle to a (smart) enum value. Caller must free the GCHandle.
		static IntPtr ConvertNSStringToSmartEnum(IntPtr value, IntPtr type)
		{
			var smart_type = (Type)GetGCHandleTarget(type)!;
			var str = GetNSObject<NSString>(value)!;
			MethodBase getConstantMethod, getValueMethod;
			if (!Registrar.IsSmartEnum(smart_type, out getConstantMethod, out getValueMethod))
				throw ErrorHelper.CreateError(8024, $"Could not find a valid extension type for the smart enum '{smart_type.FullName}'. Please file a bug at https://github.com/xamarin/xamarin-macios/issues/new.");
			var rv = ((MethodInfo)getValueMethod).Invoke(null, new object[] { str });
			return AllocGCHandle(rv);
		}

		#region Wrappers for delegate callbacks
		static void RegisterAssembly(IntPtr a)
		{
			RegisterAssembly((Assembly)GetGCHandleTarget(a)!);
		}

		static void RegisterEntryAssembly(IntPtr a)
		{
			RegisterEntryAssembly((Assembly)GetGCHandleTarget(a)!);
		}

		static void ThrowNSException(IntPtr ns_exception)
		{
#if MONOMAC
			throw new ObjCException (new NSException (ns_exception));
#else
			throw new MonoTouchException(new NSException(ns_exception));
#endif
		}

		static void RethrowManagedException(IntPtr exception_gchandle)
		{
			var e = (Exception)GCHandle.FromIntPtr((IntPtr)exception_gchandle).Target!;
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
		}

		static IntPtr CreateNSException(IntPtr ns_exception)
		{
			Exception ex;
#if MONOMAC
			ex = new ObjCException (Runtime.GetNSObject<NSException> (ns_exception)!);
#else
			ex = new MonoTouchException(Runtime.GetNSObject<NSException>(ns_exception)!);
#endif
			return GCHandle.ToIntPtr(GCHandle.Alloc(ex));
		}

		static IntPtr CreateRuntimeException(int code, IntPtr message)
		{
			var ex = ErrorHelper.CreateError(code, Marshal.PtrToStringAuto(message)!);
			return GCHandle.ToIntPtr(GCHandle.Alloc(ex));
		}

		static IntPtr UnwrapNSException(IntPtr exc_handle)
		{
			var obj = GCHandle.FromIntPtr(exc_handle).Target;
#if MONOMAC
			var exc = obj as ObjCException;
#else
			var exc = obj as MonoTouchException;
#endif
			var nsexc = exc?.NSException;
			if (nsexc is not null)
			{
				return nsexc.DangerousRetain().DangerousAutorelease().Handle;
			}
			else
			{
				return IntPtr.Zero;
			}
		}

		static IntPtr GetBlockWrapperCreator(IntPtr method, int parameter)
		{
			return AllocGCHandle(GetBlockWrapperCreator((MethodInfo)GetGCHandleTarget(method)!, parameter));
		}

		static IntPtr CreateBlockProxy(IntPtr method, IntPtr block)
		{
			return AllocGCHandle(CreateBlockProxy((MethodInfo)GetGCHandleTarget(method)!, block));
		}

		static IntPtr CreateDelegateProxy(IntPtr method, IntPtr @delegate, IntPtr signature, uint token_ref)
		{
			return BlockLiteral.GetBlockForDelegate((MethodInfo)GetGCHandleTarget(method)!, GetGCHandleTarget(@delegate), token_ref, Marshal.PtrToStringAuto(signature));
		}

		static IntPtr GetExceptionMessage(IntPtr exception_gchandle)
		{
			var exc = (Exception)GetGCHandleTarget(exception_gchandle)!;
			return Marshal.StringToHGlobalAuto(exc.Message);
		}

		static void PrintException(Exception exc, bool isInnerException, StringBuilder sb)
		{
			if (isInnerException)
				sb.AppendLine(" --- inner exception ---");
			sb.Append(exc.Message).Append(" (").Append(exc.GetType().FullName).AppendLine(")");
			var trace = exc.StackTrace;
			if (!string.IsNullOrEmpty(trace))
				sb.AppendLine(trace);
		}

		static IntPtr PrintAllExceptions(IntPtr exception_gchandle)
		{
			var str = new StringBuilder();
			try
			{
				var exc = (Exception)GetGCHandleTarget(exception_gchandle)!;

				int counter = 0;
				do
				{
					PrintException(exc, counter > 0, str);
					exc = exc.InnerException;
				} while (counter < 10 && exc is not null);
			}
			catch (Exception exception)
			{
				str.Append("Failed to print exception: ").Append(exception);
			}

			return Marshal.StringToHGlobalAuto(str.ToString());
		}

		static unsafe Assembly? GetEntryAssembly()
		{
			var asm = Assembly.GetEntryAssembly();
#if MONOMAC
			if (asm is null)
				asm = Assembly.LoadFile (Marshal.PtrToStringAuto (options->EntryAssemblyPath)!);
#endif
			return asm;
		}

		// This method will register all assemblies referenced by the entry assembly.
		// For XM it will also register all assemblies loaded in the current appdomain.
		internal static void RegisterAssemblies()
		{
#if PROFILE
			var watch = new Stopwatch ();
#endif

			RegisterEntryAssembly(GetEntryAssembly());

#if PROFILE
			Console.WriteLine ("RegisterAssemblies completed in {0} ms", watch.ElapsedMilliseconds);
#endif
		}

		// This method will register all assemblies referenced by the entry assembly.
		// For XM it will also register all assemblies loaded in the current appdomain.
		//
		// NOTE: the linker will remove this method when the dynamic registrar has been optimized away (RemoveCode.cs)
		// and as such cannot be renamed without updating the linker
		internal static void RegisterEntryAssembly(Assembly? entry_assembly)
		{
			var assemblies = new List<Assembly>();

			assemblies.Add(NSObject.PlatformAssembly); // make sure our platform assembly comes first
													   // Recursively get all assemblies referenced by the entry assembly.
			if (entry_assembly is not null)
			{
				var register_entry_assembly = true;
#if MONOMAC
				register_entry_assembly = OnAssemblyRegistration (entry_assembly.GetName ());
#endif
				if (register_entry_assembly)
					CollectReferencedAssemblies(assemblies, entry_assembly);
			}
			else
			{
				NSLog("Could not find the entry assembly.");
			}

#if MONOMAC
			// Add all assemblies already loaded
			foreach (var a in AppDomain.CurrentDomain.GetAssemblies ()) {
				if (!OnAssemblyRegistration (a.GetName ()))
					continue;

				if (!assemblies.Contains (a))
					assemblies.Add (a);
			}
#endif

			foreach (var a in assemblies)
				RegisterAssembly(a);
		}

		static void CollectReferencedAssemblies(List<Assembly> assemblies, Assembly assembly)
		{
			assemblies.Add(assembly);
			foreach (var rf in assembly.GetReferencedAssemblies())
			{
#if MONOMAC
				if (!OnAssemblyRegistration (rf))
					continue;
#endif
				try
				{
					var a = Assembly.Load(rf);
					if (!assemblies.Contains(a))
						CollectReferencedAssemblies(assemblies, a);
				}
				catch (FileNotFoundException fefe)
				{
					// that's more important for XI because device builds don't go thru this step
					// and we can end up with simulator-only failures - bug #29211
					NSLog("Could not find `{0}` referenced by assembly `{1}`.", fefe.FileName, assembly.FullName);
#if MONOMAC && !NET
					if (!NSApplication.IgnoreMissingAssembliesDuringRegistration)
						throw;
#endif
				}
			}
		}

		internal static IEnumerable<Assembly> GetAssemblies()
		{
			return Registrar.GetAssemblies();
		}

		internal static string ComputeSignature(MethodInfo method, bool isBlockSignature)
		{
			return Registrar.ComputeSignature(method, isBlockSignature);
		}

		[BindingImpl(BindingImplOptions.Optimizable)]
		public static void RegisterAssembly(Assembly a)
		{
			if (a is null)
				throw new ArgumentNullException(nameof(a));

			if (!DynamicRegistrationSupported)
				throw ErrorHelper.CreateError(8026, "Runtime.RegisterAssembly is not supported when the dynamic registrar has been linked away.");
#if MONOMAC
			var attributes = a.GetCustomAttributes (typeof (RequiredFrameworkAttribute), false);

			foreach (var attribute in attributes) {
				var requiredFramework = (RequiredFrameworkAttribute)attribute;
				string libPath;
				string libName = requiredFramework.Name;

				if (libName.Contains (".dylib")) {
					libPath = ResourcesPath!;
				}
				else {
					libPath = FrameworksPath!;
					libPath = Path.Combine (libPath, libName);
					libName = libName.Replace (".frameworks", "");
				}
				libPath = Path.Combine (libPath, libName);

				if (Dlfcn.dlopen (libPath, 0) == IntPtr.Zero)
					throw new Exception (string.Format ("Unable to load required framework: '{0}'", requiredFramework.Name),
						new Exception (Dlfcn.dlerror()));
			}

			attributes = a.GetCustomAttributes (typeof (DelayedRegistrationAttribute), false);
			foreach (var attribute in attributes) {
				var delayedRegistration = (DelayedRegistrationAttribute) attribute;
				if (delayedRegistration.Delay)
					return;
			}
#endif

			if (assemblies is null)
			{
				assemblies = new List<Assembly>();
				Class.Register(typeof(NSObject));
			}

			if (assemblies.Contains(a))
				return;

			assemblies.Add(a);

#if PROFILE
			var watch = new Stopwatch ();
			watch.Start ();
#endif

			Registrar.RegisterAssembly(a);

#if PROFILE
			watch.Stop ();
			Console.WriteLine ("RegisterAssembly ({0}) completed in {1} ms", a.FullName, watch.ElapsedMilliseconds);
#endif
		}

		static IntPtr GetClass(IntPtr klass)
		{
			return AllocGCHandle(new Class(klass));
		}

		static IntPtr GetSelector(IntPtr sel)
		{
			return AllocGCHandle(new Selector(sel));
		}

		static void GetMethodForSelector(IntPtr cls, IntPtr sel, bool is_static, IntPtr desc)
		{
			// This is called by the old registrar code.
			Registrar.GetMethodDescription(Class.Lookup(cls), sel, is_static, desc);
		}

		static bool HasNSObject(IntPtr ptr)
		{
			return TryGetNSObject(ptr, evenInFinalizerQueue: false) is not null;
		}

		static IntPtr GetHandleForINativeObject(IntPtr ptr)
		{
			return ((INativeObject)GetGCHandleTarget(ptr)!).Handle;
		}

		static void UnregisterNSObject(IntPtr native_obj, IntPtr managed_obj)
		{
			NativeObjectHasDied(native_obj, GetGCHandleTarget(managed_obj) as NSObject);
		}

		static unsafe IntPtr GetMethodFromToken(uint token_ref)
		{
			var method = Class.ResolveMethodTokenReference(token_ref);
			if (method is not null)
				return AllocGCHandle(method);

			return IntPtr.Zero;
		}

		static unsafe IntPtr GetGenericMethodFromToken(IntPtr obj, uint token_ref)
		{
			var method = Class.ResolveMethodTokenReference(token_ref);
			if (method is null)
				return IntPtr.Zero;

			var nsobj = GetGCHandleTarget(obj) as NSObject;
			if (nsobj is null)
				throw ErrorHelper.CreateError(8023, $"An instance object is required to construct a closed generic method for the open generic method: {method.DeclaringType!.FullName}.{method.Name} (token reference: 0x{token_ref:X}). Please file a bug report at https://github.com/xamarin/xamarin-macios/issues/new.");

			return AllocGCHandle(FindClosedMethod(nsobj.GetType(), method));
		}

		static IntPtr TryGetOrConstructNSObjectWrapped(IntPtr ptr)
		{
			return AllocGCHandle(GetNSObject(ptr, MissingCtorResolution.Ignore, true));
		}

		static IntPtr GetINativeObject_Dynamic(IntPtr ptr, bool owns, IntPtr type_ptr)
		{
			/*
			 * This method is called from marshalling bridge (dynamic mode).
			 */
			var type = (System.Type)GetGCHandleTarget(type_ptr)!;
			return AllocGCHandle(GetINativeObject(ptr, owns, type));
		}

		static IntPtr GetINativeObject_Static(IntPtr ptr, bool owns, uint iface_token, uint implementation_token)
		{
			/* 
			 * This method is called from generated code from the static registrar.
			 */

			var iface = Class.ResolveTypeTokenReference(iface_token)!;
			var type = Class.ResolveTypeTokenReference(implementation_token);
			return AllocGCHandle(GetINativeObject(ptr, owns, iface, type));
		}

		static IntPtr GetNSObjectWithType(IntPtr ptr, IntPtr type_ptr, out bool created)
		{
			var type = (System.Type)GetGCHandleTarget(type_ptr)!;
			return AllocGCHandle(GetNSObject(ptr, type, MissingCtorResolution.ThrowConstructor1NotFound, true, out created));
		}

		static void Dispose(IntPtr gchandle)
		{
			((IDisposable?)GetGCHandleTarget(gchandle))?.Dispose();
		}

		static bool IsParameterTransient(IntPtr info, int parameter)
		{
			var minfo = GetGCHandleTarget(info) as MethodInfo;
			if (minfo is null)
				return false; // might be a ConstructorInfo (bug #15583), but we don't care about that (yet at least).
			minfo = minfo.GetBaseDefinition();
			var parameters = minfo.GetParameters();
			if (parameters.Length <= parameter)
				return false;
			return parameters[parameter].IsDefined(typeof(TransientAttribute), false);
		}

		static bool IsParameterOut(IntPtr info, int parameter)
		{
			var minfo = GetGCHandleTarget(info) as MethodInfo;
			if (minfo is null)
				return false; // might be a ConstructorInfo (bug #15583), but we don't care about that (yet at least).
			minfo = minfo.GetBaseDefinition();
			var parameters = minfo.GetParameters();
			if (parameters.Length <= parameter)
				return false;
			return parameters[parameter].IsOut;
		}

		static void GetMethodAndObjectForSelector(IntPtr klass, IntPtr sel, bool is_static, IntPtr obj, ref IntPtr mthis, IntPtr desc)
		{
			Registrar.GetMethodDescriptionAndObject(Class.Lookup(klass), sel, is_static, obj, ref mthis, desc);
		}

		// If inner_exception_gchandle is provided, it will be freed.
		static IntPtr CreateProductException(int code, IntPtr inner_exception_gchandle, string msg)
		{
			Exception? inner_exception = null;
			if (inner_exception_gchandle != IntPtr.Zero)
			{
				GCHandle gchandle = GCHandle.FromIntPtr(inner_exception_gchandle);
				inner_exception = (Exception?)gchandle.Target;
				gchandle.Free();
			}
			Exception ex;
			if (inner_exception is not null)
			{
				ex = ErrorHelper.CreateError(code, inner_exception, msg);
			}
			else
			{
				ex = ErrorHelper.CreateError(code, msg);
			}
			return GCHandle.ToIntPtr(GCHandle.Alloc(ex, GCHandleType.Normal));
		}

		static IntPtr TypeGetFullName(IntPtr type)
		{
			return Marshal.StringToHGlobalAuto(((Type)GetGCHandleTarget(type)!).FullName);
		}

		static IntPtr GetObjectTypeFullName(IntPtr gchandle)
		{
			var obj = GetGCHandleTarget(gchandle);
			if (obj is null)
				return IntPtr.Zero;
			return Marshal.StringToHGlobalAuto(obj.GetType().FullName);
		}

		static IntPtr LookupManagedTypeName(IntPtr klass)
		{
			return Marshal.StringToHGlobalAuto(Class.LookupFullName(klass));
		}
		#endregion

		static MethodInfo? GetBlockProxyAttributeMethod(MethodInfo method, int parameter)
		{
			var attrs = method.GetParameters()[parameter].GetCustomAttributes(typeof(BlockProxyAttribute), true);
			if (attrs.Length == 1)
			{
				try
				{
					var attr = attrs[0] as BlockProxyAttribute;
					return attr?.Type?.GetMethod("Create");
				}
				catch
				{
					return null;
				}
			}
			return null;
		}

		internal static ProtocolMemberAttribute? GetProtocolMemberAttribute(Type type, string selector, MethodInfo method)
		{
			var memberAttributes = type.GetCustomAttributes<ProtocolMemberAttribute>();
			if (memberAttributes is null)
				return null;

			foreach (var attrib in memberAttributes)
			{
				if (attrib.IsStatic != method.IsStatic)
					continue;

				if (attrib.Selector != selector)
					continue;

				if (!attrib.IsProperty)
				{
					var methodParameters = method.GetParameters();
					if ((attrib.ParameterType?.Length ?? 0) != methodParameters.Length)
						continue;
					var notApplicable = false;
					for (int i = 0; i < methodParameters.Length; i++)
					{
						var paramType = methodParameters[i].ParameterType;
						var isByRef = paramType.IsByRef;
						if (isByRef)
							paramType = paramType.GetElementType();
						if (isByRef != attrib.ParameterByRef![i])
						{
							notApplicable = true;
							break;
						}
						if (paramType != attrib.ParameterType![i])
						{
							notApplicable = true;
							break;
						}
					}
					if (notApplicable)
						continue;
				}

				return attrib;
			}

			return null;
		}

		//
		// Returns a MethodInfo that represents the method that can be used to turn
		// a the block in the given method at the given parameter into a strongly typed
		// delegate
		//
		[EditorBrowsable(EditorBrowsableState.Never)]
		static MethodInfo? GetBlockWrapperCreator(MethodInfo method, int parameter)
		{
			// A mirror of this method is also implemented in StaticRegistrar:FindBlockProxyCreatorMethod
			// If this method is changed, that method will probably have to be updated too (tests!!!)
			MethodInfo first = method;
			MethodInfo? last = null;
			Type[]? extensionParameters = null;

			while (method != last)
			{
				last = method;
				var createMethod = GetBlockProxyAttributeMethod(method, parameter);
				if (createMethod is not null)
					return createMethod;
				method = method.GetBaseDefinition();
			}

			string? selector = null;

			// Might be the implementation of an interface method, so find the corresponding
			// MethodInfo for the interface, and check for BlockProxy attributes there as well.
			foreach (var iface in method.DeclaringType!.GetInterfaces())
			{
				if (!iface.IsDefined(typeof(ProtocolAttribute), false))
					continue;

				var map = method.DeclaringType.GetInterfaceMap(iface);
				for (int i = 0; i < map.TargetMethods.Length; i++)
				{
					if (map.TargetMethods[i] == first)
					{
						var createMethod = GetBlockProxyAttributeMethod(map.InterfaceMethods[i], parameter);
						if (createMethod is not null)
							return createMethod;
					}
				}

				// We store the BlockProxy type in the ProtocolMemberAttribute, so check those.
				// We may run into binding assemblies built with earlier versions of the generator,
				// which means we can't rely on finding the BlockProxy attribute in the ProtocolMemberAttribute.
				if (selector is null)
					selector = GetExportAttribute(method)?.Selector ?? string.Empty;
				if (!string.IsNullOrEmpty(selector))
				{
					var attrib = GetProtocolMemberAttribute(iface, selector, method);
					if (attrib is not null && attrib.ParameterBlockProxy!.Length > parameter && attrib.ParameterBlockProxy[parameter] is not null)
						return attrib.ParameterBlockProxy[parameter]!.GetMethod("Create");
				}

				// Might be an implementation of an optional protocol member.
				// We look that up on the corresponding extension method.
				string extensionName = string.Empty;
				if (!string.IsNullOrEmpty(iface.Namespace))
					extensionName = iface.Namespace + ".";
				extensionName += iface.Name.Substring(1) + "_Extensions";
				var extensionType = iface.Assembly.GetType(extensionName, false);
				if (extensionType is not null)
				{
					if (extensionParameters is null)
					{
						var methodParameters = method.GetParameters();
						extensionParameters = new Type[methodParameters.Length + 1];
						for (int i = 0; i < methodParameters.Length; i++)
							extensionParameters[i + 1] = methodParameters[i].ParameterType;
					}
					extensionParameters[0] = iface;
					var extensionMethod = extensionType.GetMethod(method.Name, BindingFlags.Public | BindingFlags.Static, null, extensionParameters, null);
					if (extensionMethod is not null)
					{
						var createMethod = GetBlockProxyAttributeMethod(extensionMethod, parameter + 1);
						if (createMethod is not null)
							return createMethod;
					}
				}
			}

			throw new RuntimeException(8009, true, "Unable to locate the block to delegate conversion method for the method {0}.{1}'s parameter #{2}. Please file a bug at https://github.com/xamarin/xamarin-macios/issues/new.",
				method.DeclaringType.FullName, method.Name, parameter + 1);
		}

		//
		// Called from the runtime, since it is too hard to use the unmanaged API
		// Given a MethodInfo, invoke it, passing the given block
		//
		// Used to call the Create(IntPtr) method on the proxy classes that turn
		// objective c blocks into strongly typed delegates.
		//
		[EditorBrowsable(EditorBrowsableState.Never)]
		static Delegate CreateBlockProxy(MethodInfo method, IntPtr block)
		{
			return (Delegate)method.Invoke(null, new object[] { block })!;
		}

		internal static Delegate? GetDelegateForBlock(IntPtr methodPtr, Type type)
		{
			// We do not care if there is a race condition and we initialize two caches
			// since the worst that can happen is that we end up with an extra
			// delegate->function pointer.
			Delegate? val;
			var pair = new IntPtrTypeValueTuple(methodPtr, type);
			lock (lock_obj)
			{
				if (block_to_delegate_cache is null)
					block_to_delegate_cache = new Dictionary<IntPtrTypeValueTuple, Delegate>();

				if (block_to_delegate_cache.TryGetValue(pair, out val))
					return val;
			}

			val = Marshal.GetDelegateForFunctionPointer(methodPtr, type);

			lock (lock_obj)
			{
				block_to_delegate_cache[pair] = val;
			}
			return val;
		}

		unsafe static MethodBase FindMethod(IntPtr typeptr, IntPtr methodptr, int paramCount, IntPtr* paramptr)
		{
			var type = Type.GetType(Marshal.PtrToStringAuto(typeptr)!)!;
			var methodName = Marshal.PtrToStringAuto(methodptr)!;
			var parameterTypes = new string[paramCount];
			for (int i = 0; i < paramCount; i++)
				parameterTypes[i] = Marshal.PtrToStringAuto(paramptr[i])!;

			MethodBase[] methods;
			if (methodName == ".ctor")
			{
				methods = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			}
			else
			{
				methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			}

			foreach (var m in methods)
			{
				if (m.Name != methodName)
					continue;
				var parameters = m.GetParameters();
				if (parameters.Length != paramCount)
					continue;
				bool found = true;
				for (int i = 0; i < paramCount; i++)
				{
					var paramType = parameters[i].ParameterType;

					var ptaqn = paramType.AssemblyQualifiedName;
					if (ptaqn is null)
						continue;
					// the condensed string representation needs some fixup if there are generics used
					if (paramType.IsGenericType)
					{
						int s = 0;
						while ((s = ptaqn.IndexOf(", Version=", s, StringComparison.OrdinalIgnoreCase)) != -1)
						{
							var e = ptaqn.IndexOf(']', s);
							ptaqn = (e == -1) ? ptaqn.Substring(0, s) : ptaqn.Remove(s, e - s);
						}
					}
					if (paramType.Name != parameterTypes[i] && !ptaqn.StartsWith(parameterTypes[i], StringComparison.Ordinal))
					{
						found = false;
						break;
					}
				}
				if (!found)
					continue;

				// Found the method.
				return m;
			}

			throw ErrorHelper.CreateError(8002, "Could not find the method '{0}' in the type '{1}'.", methodName, type.FullName);
		}

		internal static void UnregisterNSObject(IntPtr ptr)
		{
			lock (lock_obj)
			{
				if (object_map.Remove(ptr, out var value))
					value.Free();
			}
		}

		internal static void NativeObjectHasDied(IntPtr ptr, NSObject? managed_obj)
		{
			lock (lock_obj)
			{
				if (object_map.TryGetValue(ptr, out var wr))
				{
					if (managed_obj is null || wr.Target == (object)managed_obj)
					{
						object_map.Remove(ptr);
						wr.Free();
					}

				}

				if (managed_obj is not null)
					managed_obj.ClearHandle();
			}
		}

		internal static void RegisterNSObject(NSObject obj, IntPtr ptr)
		{
			var handle = GCHandle.Alloc(obj, GCHandleType.WeakTrackResurrection);
			lock (lock_obj)
			{
				object_map[ptr] = handle;
				obj.Handle = ptr;
			}
		}

		internal static PropertyInfo? FindPropertyInfo(MethodInfo accessor)
		{
			if (!accessor.IsSpecialName)
				return null;

			foreach (var pi in accessor.DeclaringType!.GetProperties())
			{
				if (pi.GetGetMethod() == accessor)
					return pi;
				if (pi.GetSetMethod() == accessor)
					return pi;
			}

			return null;
		}

		internal static ExportAttribute? GetExportAttribute(MethodInfo method)
		{
			var attrib = method.GetCustomAttribute<ExportAttribute>();
			if (attrib is null)
			{
				var pinfo = FindPropertyInfo(method);
				if (pinfo is not null)
					attrib = pinfo.GetCustomAttribute<ExportAttribute>();
			}
			return attrib;
		}

		static NSObject? IgnoreConstructionError(IntPtr ptr, IntPtr klass, Type type)
		{
			return null;
		}

		internal enum MissingCtorResolution
		{
			ThrowConstructor1NotFound,
			ThrowConstructor2NotFound,
			Ignore,
		}

		static void MissingCtor(IntPtr ptr, IntPtr klass, Type type, MissingCtorResolution resolution)
		{
			string msg;

			if (klass == IntPtr.Zero)
				klass = Class.GetClassForObject(ptr);

			switch (resolution)
			{
				case MissingCtorResolution.ThrowConstructor1NotFound:
					msg = "Failed to marshal the Objective-C object 0x{0} (type: {1}). " +
						"Could not find an existing managed instance for this object, " +
						"nor was it possible to create a new managed instance " +
#if NET
					"(because the type '{2}' does not have a constructor that takes one NativeHandle argument).";
#else
					"(because the type '{2}' does not have a constructor that takes one IntPtr argument).";
#endif
					break;
				case MissingCtorResolution.ThrowConstructor2NotFound:
					msg = "Failed to marshal the Objective-C object 0x{0} (type: {1}). " +
						"Could not find an existing managed instance for this object, " +
						"nor was it possible to create a new managed instance " +
#if NET
					"(because the type '{2}' does not have a constructor that takes two (NativeHandle, bool) arguments).";
#else
					"(because the type '{2}' does not have a constructor that takes two (IntPtr, bool) arguments).";
#endif
					break;
				case MissingCtorResolution.Ignore:
				default:
					return;
			}

			throw ErrorHelper.CreateError(8027, string.Format(msg, ptr.ToString("x"), new Class(klass).Name, type.FullName));
		}

		static NSObject? ConstructNSObject(IntPtr ptr, IntPtr klass, MissingCtorResolution missingCtorResolution)
		{
			Type type = Class.Lookup(klass);

			if (type is not null)
			{
				return ConstructNSObject<NSObject>(ptr, type, missingCtorResolution);
			}
			else
			{
				return new NSObject(ptr);
			}
		}

		internal static T? ConstructNSObject<T>(IntPtr ptr) where T : NSObject
		{
			return ConstructNSObject<T>(ptr, typeof(T), MissingCtorResolution.ThrowConstructor1NotFound);
		}

		// The generic argument T is only used to cast the return value.
		// The 'selector' and 'method' arguments are only used in error messages.
		static T? ConstructNSObject<T>(IntPtr ptr, Type type, MissingCtorResolution missingCtorResolution) where T : class, INativeObject
		{
			if (type is null)
				throw new ArgumentNullException(nameof(type));

			var ctor = GetIntPtrConstructor(type);

			if (ctor is null)
			{
				MissingCtor(ptr, IntPtr.Zero, type, missingCtorResolution);
				return null;
			}

			var ctorArguments = new object[1];
#if NET
			ctorArguments [0] = new NativeHandle (ptr);
#else
			ctorArguments[0] = ptr;
#endif

			return (T)ctor.Invoke(ctorArguments);
		}

		// The generic argument T is only used to cast the return value.
		static T? ConstructINativeObject<T>(IntPtr ptr, bool owns, Type type, MissingCtorResolution missingCtorResolution) where T : class, INativeObject
		{
			if (type is null)
				throw new ArgumentNullException(nameof(type));

			if (type.IsByRef)
				type = type.GetElementType()!;

			var ctor = GetIntPtr_BoolConstructor(type);

			if (ctor is null)
			{
				MissingCtor(ptr, IntPtr.Zero, type, missingCtorResolution);
				return null;
			}

			var ctorArguments = new object[2];
#if NET
			ctorArguments [0] = new NativeHandle (ptr);
#else
			ctorArguments[0] = ptr;
#endif
			ctorArguments[1] = owns;

			return (T?)ctor.Invoke(ctorArguments);
		}

		static IntPtr CreateNSObject(IntPtr type_gchandle, IntPtr handle, NSObject.Flags flags)
		{
			return NSObject.CreateNSObject(type_gchandle, handle, flags);
		}

		static ConstructorInfo? GetIntPtrConstructor(Type type)
		{
			lock (intptr_ctor_cache)
			{
				if (intptr_ctor_cache.TryGetValue(type, out var rv))
					return rv;
			}
			var ctors = type.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
			for (int i = 0; i < ctors.Length; ++i)
			{
				var param = ctors[i].GetParameters();
				if (param.Length != 1)
					continue;
#if NET
				if (param [0].ParameterType != typeof (NativeHandle))
#else
				if (param[0].ParameterType != typeof(IntPtr))
#endif
					continue;

				lock (intptr_ctor_cache)
					intptr_ctor_cache[type] = ctors[i];
				return ctors[i];
			}
			return null;
		}

		static ConstructorInfo? GetIntPtr_BoolConstructor(Type type)
		{
			lock (intptr_bool_ctor_cache)
			{
				if (intptr_bool_ctor_cache.TryGetValue(type, out var rv))
					return rv;
			}
			var ctors = type.GetConstructors(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
			for (int i = 0; i < ctors.Length; ++i)
			{
				var param = ctors[i].GetParameters();
				if (param.Length != 2)
					continue;
#if NET
				if (param [0].ParameterType != typeof (NativeHandle))
#else
				if (param[0].ParameterType != typeof(IntPtr))
#endif
					continue;
				if (param[1].ParameterType != typeof(bool))
					continue;

				lock (intptr_bool_ctor_cache)
					intptr_bool_ctor_cache[type] = ctors[i];
				return ctors[i];
			}
			return null;
		}

		public static NSObject? TryGetNSObject(IntPtr ptr)
		{
			return TryGetNSObject(ptr, evenInFinalizerQueue: false);
		}

		internal static NSObject? TryGetNSObject(IntPtr ptr, bool evenInFinalizerQueue)
		{
			lock (lock_obj)
			{
				if (object_map.TryGetValue(ptr, out var reference))
				{
					var target = (NSObject?)reference.Target;
					if (target is null)
						return null;

					if (target.InFinalizerQueue)
					{
						if (!evenInFinalizerQueue)
						{
							// Don't return objects that's been queued for finalization unless requested to.
							return null;
						}

						if (target.IsDirectBinding && !target.IsRegisteredToggleRef)
						{
							// This is a non-toggled direct binding, which is safe to re-create.
							// We get here if the native object is still around, while the GC
							// has collected the managed object, and then we end up needing
							// a managed wrapper again before we've completely cleaned up 
							// the existing managed wrapper (which is the one we just found).
							// Returning null here will cause us to re-create the managed wrapper.
							// See bug #37670 for a real-world scenario.
							return null;
						}
					}

					return target;
				}
			}

			return null;
		}

		public static NSObject? GetNSObject(IntPtr ptr)
		{
			return GetNSObject(ptr, MissingCtorResolution.ThrowConstructor1NotFound);
		}

		internal static NSObject? GetNSObject(IntPtr ptr, MissingCtorResolution missingCtorResolution, bool evenInFinalizerQueue = false)
		{
			if (ptr == IntPtr.Zero)
				return null;

			var o = TryGetNSObject(ptr, evenInFinalizerQueue);

			if (o is not null)
				return o;

			return ConstructNSObject(ptr, Class.GetClassForObject(ptr), missingCtorResolution);
		}

		static public T? GetNSObject<T>(IntPtr ptr) where T : NSObject
		{
			if (ptr == IntPtr.Zero)
				return null;

			var obj = TryGetNSObject(ptr, evenInFinalizerQueue: false);
			T? o;

			if (obj is null)
			{
				// Try to get the managed type that correspond to this exact native type
				IntPtr p = Class.GetClassForObject(ptr);
				// If unknown then we'll get the Class that Lookup to NSObject even if this is not NSObject.
				// We can use this condition to fallback on the provided (generic argument) type
				Type target_type;
				if (p != NSObjectClass)
				{
					target_type = Class.Lookup(p);
					if (target_type == typeof(NSObject))
						target_type = typeof(T);
					else if (typeof(T).IsGenericType)
						target_type = typeof(T);
					else if (target_type.IsSubclassOf(typeof(T)))
					{
						// do nothing, this is fine.
					}
					else if (Messaging.bool_objc_msgSend_IntPtr(ptr, Selector.GetHandle("isKindOfClass:"), Class.GetHandle(typeof(T))))
					{
						// If the instance itself claims it's an instance of the provided (generic argument) type,
						// then we believe the instance. See bug #20692 for a test case.
						target_type = typeof(T);
					}
				}
				else
				{
					target_type = typeof(NSObject);
				}
				o = ConstructNSObject<T>(ptr, target_type, MissingCtorResolution.ThrowConstructor1NotFound);
			}
			else
			{
				o = obj as T;
				if (o is null)
					throw new InvalidCastException(string.Format("Unable to cast object of type '{0}' to type '{1}'", obj.GetType().FullName, typeof(T).FullName));
			}

			return o;
		}

		static public T? GetNSObject<T>(IntPtr ptr, bool owns) where T : NSObject
		{
			var obj = GetNSObject<T>(ptr);
			if (owns)
				obj?.DangerousRelease();
			return obj;
		}

		//
		// This method is an ugly hack.
		//
		// Existing apps (may) work even if GetNSObject doesn't return the expected type. Example:
		// https://bugzilla.xamarin.com/show_bug.cgi?id=13518 - put bluntly: we return some type
		// the caller doesn't expect, the caller does not verify the type and continues happily,
		// violating type safety.
		//
		// Sometimes this works (#13518), sometimes it doesn't (#14075).
		//
		// This GetNSObject overload takes an additional @target_type, which determines the type
		// to create if there is no wrapper type for the actual type of the native object. Note
		// in particular that we do not check in the other code paths if the type we return is
		// compatible with @target_type.
		//
		// FIXME: this hack should become redundant once we've implement support for generic
		// NSObject subclasses (the test case in #13518 should work even with type checks).
		//

		// The 'selector' and 'method' arguments are only used in error messages.
		static NSObject? GetNSObject(IntPtr ptr, Type target_type, MissingCtorResolution missingCtorResolution, bool evenInFinalizerQueue, out bool created)
		{
			created = false;

			if (ptr == IntPtr.Zero)
				return null;

			var o = TryGetNSObject(ptr, evenInFinalizerQueue);

			if (o is not null)
				return o;

			// Try to get the managed type that correspond to this exact native type
			IntPtr p = Class.GetClassForObject(ptr);
			// If unknown then we'll get the Class that Lookup to NSObject even if this is not NSObject.
			// We can use this condition to fallback on the provided (generic argument) type

			if (p != NSObjectClass)
			{
				var dynamic_type = Class.Lookup(p);
				if (dynamic_type == typeof(NSObject))
				{
					// nothing to do
				}
				else if (dynamic_type.IsSubclassOf(target_type))
				{
					target_type = dynamic_type;
				}
				else if (Messaging.bool_objc_msgSend_IntPtr(ptr, Selector.GetHandle("isKindOfClass:"), Class.GetHandle(target_type)))
				{
					// nothing to do
				}
				else
				{
					target_type = dynamic_type;
				}
			}
			else
			{
				target_type = typeof(NSObject);
			}

			created = true;
			return ConstructNSObject<NSObject>(ptr, target_type, MissingCtorResolution.ThrowConstructor1NotFound);
		}

		static Type LookupINativeObjectImplementation(IntPtr ptr, Type target_type, Type? implementation = null, bool forced_type = false)
		{
			if (!typeof(NSObject).IsAssignableFrom(target_type))
			{
				// If we're not expecting an NSObject, we can't do a dynamic lookup of the type of ptr,
				// since we do not know if the object actually supports dynamic type lookup (it can be an
				// INativeObject which is just a managed wrapper around a native handle). Example: bug #24438,
				// where we're asked to lookup a CGLContext (we crash if we call Class.GetClassForObject on
				// such a pointer).
				implementation = target_type;
			}
			else
			{
				// Lookup the ObjC type of the ptr and see if we can use it.
				var p = Class.GetClassForObject(ptr);

				if (p == NSObjectClass)
				{
					if (implementation is null)
						implementation = target_type;
				}
				else
				{
					// only throw if we're not forcing the type we want to expose
					var runtime_type = Class.Lookup(p, throw_on_error: !forced_type);
					// Check if the runtime type can actually be used.
					if (target_type.IsAssignableFrom(runtime_type))
					{
						implementation = runtime_type;
					}
					else if (implementation is null)
					{
						implementation = target_type;
					}
				}
			}

			var interface_check_type = implementation;
#if NET
			// https://github.com/dotnet/runtime/issues/39068
			if (interface_check_type.IsByRef)
				interface_check_type = interface_check_type.GetElementType ();
#endif

			if (interface_check_type!.IsInterface)
				implementation = FindProtocolWrapperType(implementation);

			return implementation!;
		}

		public static INativeObject? GetINativeObject(IntPtr ptr, bool owns, Type target_type)
		{
			return GetINativeObject(ptr, owns, target_type, null);
		}

		// this method is identical in behavior to the generic one.
		static INativeObject? GetINativeObject(IntPtr ptr, bool owns, Type target_type, Type? implementation)
		{
			if (ptr == IntPtr.Zero)
				return null;

			var o = TryGetNSObject(ptr, evenInFinalizerQueue: false);
			if (o is not null && target_type.IsAssignableFrom(o.GetType()))
			{
				// found an existing object with the right type.
				return o;
			}

			if (o is not null)
			{
				var interface_check_type = target_type;
#if NET
				// https://github.com/dotnet/runtime/issues/39068
				if (interface_check_type.IsByRef)
					interface_check_type = interface_check_type.GetElementType ()!;
#endif
				// found an existing object, but with an incompatible type.
				if (!interface_check_type.IsInterface)
				{
					// if the target type is another class, there's nothing we can do.
					throw new InvalidCastException(string.Format("Unable to cast object of type '{0}' to type '{1}'.", o.GetType().FullName, target_type.FullName));
				}
			}

			// Lookup the ObjC type of the ptr and see if we can use it.
			implementation = LookupINativeObjectImplementation(ptr, target_type, implementation);

			if (implementation.IsSubclassOf(typeof(NSObject)))
			{
				if (o is not null)
				{
					// We already have an instance of an NSObject-subclass for this ptr.
					// Creating another will break the one-to-one assumption we have between
					// native objects and NSObject instances.
					throw ErrorHelper.CreateError(8004, "Cannot create an instance of {0} for the native object 0x{1} (of type '{2}'), " +
						"because another instance already exists for this native object (of type {3}).",
						implementation.FullName, ptr.ToString("x"), Class.class_getName(Class.GetClassForObject(ptr)), o.GetType().FullName);
				}
				return ConstructNSObject<INativeObject>(ptr, implementation, MissingCtorResolution.ThrowConstructor1NotFound);
			}

			return ConstructINativeObject<INativeObject>(ptr, owns, implementation, MissingCtorResolution.ThrowConstructor2NotFound);
		}

		// this method is identical in behavior to the non-generic one.
		public static T? GetINativeObject<T>(IntPtr ptr, bool owns) where T : class, INativeObject
		{
			return GetINativeObject<T>(ptr, false, owns);
		}

		public static T? GetINativeObject<T>(IntPtr ptr, bool forced_type, bool owns) where T : class, INativeObject
		{
			if (ptr == IntPtr.Zero)
				return null;

			var o = TryGetNSObject(ptr, evenInFinalizerQueue: false);
			var t = o as T;
			if (t is not null)
			{
				// found an existing object with the right type.
				return t;
			}

			// If forced type is true, we ignore any existing instances if the managed type of the existing instance isn't compatible with T.
			// This may end up creating multiple managed wrapper instances for the same native handle,
			// which is not optimal, but sometimes the alternative can be worse :/
			if (o is not null && !forced_type)
			{
				// found an existing object, but with an incompatible type.
				if (!typeof(T).IsInterface && typeof(NSObject).IsAssignableFrom(typeof(T)))
				{
					// if the target type is another NSObject subclass, there's nothing we can do.
					throw new InvalidCastException(string.Format("Unable to cast object of type '{0}' to type '{1}'.", o.GetType().FullName, typeof(T).FullName));
				}
			}

			// Lookup the ObjC type of the ptr and see if we can use it.
			var implementation = LookupINativeObjectImplementation(ptr, typeof(T), forced_type: forced_type);

			if (implementation.IsSubclassOf(typeof(NSObject)))
			{
				if (o is not null && !forced_type)
				{
					// We already have an instance of an NSObject-subclass for this ptr.
					// Creating another will break the one-to-one assumption we have between
					// native objects and NSObject instances.
					throw ErrorHelper.CreateError(8004, "Cannot create an instance of {0} for the native object 0x{1} (of type '{2}'), " +
						"because another instance already exists for this native object (of type {3}).",
						implementation.FullName, ptr.ToString("x"), Class.class_getName(Class.GetClassForObject(ptr)), o.GetType().FullName);
				}
				return (T?)ConstructNSObject<T>(ptr, implementation, MissingCtorResolution.ThrowConstructor1NotFound);
			}

			return ConstructINativeObject<T>(ptr, owns, implementation, MissingCtorResolution.ThrowConstructor2NotFound);
		}

		static Type? FindProtocolWrapperType(Type? type)
		{
			if (type is null)
				return null;
#if NET
			// https://github.com/dotnet/runtime/issues/39068
			if (type.IsByRef)
				type = type.GetElementType ()!;
#endif
			if (!type.IsInterface)
				return null;

			// Check if the static registrar knows about this protocol
			unsafe
			{
				var map = options->RegistrationMap;
				if (map is not null)
				{
					var token = Class.GetTokenReference(type, throw_exception: false);
					if (token != INVALID_TOKEN_REF)
					{
						var wrapper_token = xamarin_find_protocol_wrapper_type(token);
						if (wrapper_token != INVALID_TOKEN_REF)
							return Class.ResolveTypeTokenReference(wrapper_token);
					}
				}
			}

			// need to look up the type from the ProtocolAttribute.
			var a = type.GetCustomAttributes(typeof(Foundation.ProtocolAttribute), false);

			var attr = (Foundation.ProtocolAttribute?)(a.Length > 0 ? a[0] : null);
			if (attr is null || attr.WrapperType is null)
				throw ErrorHelper.CreateError(4125, "The registrar found an invalid interface '{0}': " +
					"The interface must have a Protocol attribute specifying its wrapper type.",
					type.FullName);
			return attr.WrapperType;
		}

		[DllImport("__Internal")]
		extern static uint xamarin_find_protocol_wrapper_type(uint token_ref);

		public static IntPtr GetProtocol(string protocol)
		{
			return Protocol.objc_getProtocol(protocol);
		}

		internal static IntPtr GetProtocolForType(Type type)
		{
			// Check if the static registrar knows about this protocol
			unsafe
			{
				var map = options->RegistrationMap;
				if (map is not null && map->protocol_count > 0)
				{
					var token = Class.GetTokenReference(type);
					var tokens = map->protocol_map.protocol_tokens;
					for (int i = 0; i < map->protocol_count; i++)
					{
						if (tokens[i] == token)
							return map->protocol_map.protocols[i];
					}
				}
			}

			if (type.IsInterface)
			{
				var pa = type.GetCustomAttribute<ProtocolAttribute>(false);
				if (pa is not null)
				{
					var handle = Protocol.objc_getProtocol(pa.Name);
					if (handle != IntPtr.Zero)
						return handle;
				}
			}

			throw new ArgumentException(string.Format("'{0}' is an unknown protocol", type.FullName));
		}

		[BindingImpl(BindingImplOptions.Optimizable)]
		internal static bool IsUserType(IntPtr self)
		{
			var cls = Class.object_getClass(self);

			unsafe
			{
				if (options->RegistrationMap is not null && options->RegistrationMap->map_count > 0)
				{
					var map = options->RegistrationMap->map;
					var idx = FindUserTypeIndex(map, 0, options->RegistrationMap->map_count - 1, cls);
					if (idx >= 0)
						return (map[idx].flags & MTTypeFlags.UserType) == MTTypeFlags.UserType;
					// If using the partial static registrar, we need to continue
					// If full static registrar, we can return false, as long as the dynamic registrar is not supported
					if (!DynamicRegistrationSupported && (options->Flags & InitializationFlags.IsPartialStaticRegistrar) != InitializationFlags.IsPartialStaticRegistrar)
						return false;
				}
			}

			return Class.class_getInstanceMethod(cls, Selector.GetHandle("xamarinSetGCHandle:flags:")) != IntPtr.Zero;
		}

		static unsafe int FindUserTypeIndex(MTClassMap* map, int lo, int hi, IntPtr cls)
		{
			if (hi >= lo)
			{
				int mid = lo + (hi - lo) / 2;

				if (map[mid].handle == cls)
					return mid;

				if ((long)map[mid].handle > (long)cls)
					return FindUserTypeIndex(map, lo, mid - 1, cls);

				return FindUserTypeIndex(map, mid + 1, hi, cls);
			}

			return -1;
		}

		public static void ConnectMethod(Type type, MethodInfo method, Selector selector)
		{
			if (selector is null)
				throw new ArgumentNullException(nameof(selector));

			ConnectMethod(type, method, new ExportAttribute(selector.Name));
		}

		[BindingImpl(BindingImplOptions.Optimizable)]
		public static void ConnectMethod(Type type, MethodInfo method, ExportAttribute export)
		{
			if (type is null)
				throw new ArgumentNullException(nameof(type));

			if (method is null)
				throw new ArgumentNullException(nameof(method));

			if (export is null)
				throw new ArgumentNullException(nameof(export));

			if (!DynamicRegistrationSupported)
				throw ErrorHelper.CreateError(8026, "Runtime.ConnectMethod is not supported when the dynamic registrar has been linked away.");

			Registrar.RegisterMethod(type, method, export);
		}

		public static void ConnectMethod(MethodInfo method, Selector selector)
		{
			if (method is null)
				throw new ArgumentNullException(nameof(method));

			ConnectMethod(method.DeclaringType!, method, selector);
		}

		[DllImport("__Internal", CharSet = CharSet.Unicode)]
		extern static void xamarin_log(string s);

		[DllImport(Constants.libcLibrary)]
		extern static nint write(int filedes, byte[] buf, nint nbyte);

		internal static void NSLog(string value)
		{
			try
			{
				xamarin_log(value);
			}
			catch
			{
				// Append a newline like NSLog does
				if (!value.EndsWith('\n'))
					value += "\n";
				// Don't use Console.WriteLine, since that brings in a lot of supporting code and may bloat apps.
				var utf8 = Encoding.UTF8.GetBytes(value);
				write(2 /* STDERR */, utf8, utf8.Length);
				// Ignore any errors writing to stderr (might happen on devices if the developer tools haven't been mounted, but xamarin_log should always work on devices).
			}
		}

		internal static void NSLog(string format, params object?[] args)
		{
			NSLog(string.Format(format, args));
		}

		internal static string ToFourCCString(uint value)
		{
			return ToFourCCString(unchecked((int)value));
		}

		internal static string ToFourCCString(int value)
		{
			return new string(new char[] {
				(char) (byte) (value >> 24),
				(char) (byte) (value >> 16),
				(char) (byte) (value >> 8),
				(char) (byte) value });
		}
#endif // !COREBUILD

		static int MajorVersion = -1;
		static int MinorVersion = -1;
		static int BuildVersion = -1;

		internal static bool CheckSystemVersion(int major, int minor, string systemVersion)
		{
			return CheckSystemVersion(major, minor, 0, systemVersion);
		}

		internal static bool CheckSystemVersion(int major, int minor, int build, string systemVersion)
		{
			if (MajorVersion == -1)
			{
				string[] version = systemVersion.Split(new char[] { '.' });

				if (version.Length < 1 || !int.TryParse(version[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out MajorVersion))
					MajorVersion = 2;

				if (version.Length < 2 || !int.TryParse(version[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out MinorVersion))
					MinorVersion = 0;

				if (version.Length < 3 || !int.TryParse(version[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out BuildVersion))
					BuildVersion = 0;
			}

			if (MajorVersion > major)
				return true;
			else if (MajorVersion < major)
				return false;

			if (MinorVersion > minor)
				return true;
			else if (MinorVersion < minor)
				return false;

			if (BuildVersion < build)
				return false;

			return true;
		}

		internal unsafe static IntPtr CloneMemory(IntPtr source, long length)
		{
			var rv = Marshal.AllocHGlobal((IntPtr)length);
			Buffer.MemoryCopy((void*)source, (void*)rv, length, length);
			return rv;
		}

		// This function will try to compare a native UTF8 string to a managed string without creating a temporary managed string for the native UTF8 string.
		// Currently this only works if the UTF8 string only contains single-byte characters.
		// If any multi-byte characters are found, the native utf8 string is converted to a managed string, and then normal managed comparison is done.
		internal static bool StringEquals(IntPtr utf8, string? str)
		{
			if (str is null)
				return utf8 == IntPtr.Zero;

			if (utf8 == IntPtr.Zero)
				return false;

			// The vast majority of strings we compare fall within the single-byte UTF8 range, so optimize for this
			unsafe
			{
				byte* c = (byte*)utf8;
				for (int i = 0; i < str.Length; i++)
				{
					byte b = c[i];
					if (b > 0x7F)
					{
						// This string is a multibyte UTF8 string, so go the slow route and convert it to a managed string before comparison
						return string.Equals(Marshal.PtrToStringUTF8(utf8), str);
					}
					if (b != (short)str[i])
						return false;
				}
				return c[str.Length] == 0;
			}
		}

		internal static MethodInfo FindClosedMethod(Type closed_type, MethodBase open_method)
		{
			// FIXME: I think it should be handled before getting here (but it's safer here for now)
			if (!open_method.ContainsGenericParameters)
				return (MethodInfo)open_method;

			// First we need to find the type that declared the open method.
			var declaring_closed_type = closed_type;
			do
			{
				if (declaring_closed_type.IsGenericType && declaring_closed_type.GetGenericTypeDefinition() == open_method.DeclaringType)
				{
					closed_type = declaring_closed_type;
					break;
				}
				declaring_closed_type = declaring_closed_type.BaseType;
			} while (declaring_closed_type is not null);

			// Find the closed method.
			foreach (var mi in closed_type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
			{
				if (mi.MetadataToken == open_method.MetadataToken)
				{
					return mi;
				}
			}

			throw ErrorHelper.CreateError(8003, "Failed to find the closed generic method '{0}' on the type '{1}'.", open_method.Name, closed_type.FullName);
		}

		static void GCCollect()
		{
			GC.Collect();
		}

		[EditorBrowsable(EditorBrowsableState.Never)]
#if MONOMAC && !NET
		public static void ReleaseBlockOnMainThread (IntPtr block)
		{
			if (release_block_on_main_thread is null)
				release_block_on_main_thread = LookupInternalFunction<intptr_func> ("xamarin_release_block_on_main_thread");
			release_block_on_main_thread (block);
		}
		delegate void intptr_func (IntPtr block);
		static intptr_func? release_block_on_main_thread;
#else
		[DllImport("__Internal", EntryPoint = "xamarin_release_block_on_main_thread")]
		public static extern void ReleaseBlockOnMainThread(IntPtr block);
#endif

		// Throws an ArgumentNullException if 'obj' is null.
		// This method is particularly helpful when calling another constructor from a constructor, where you can't add any statements before calling the other constructor:
		//
		//     Foo (object obj)
		//         : base (Runtime.ThrowOnNull (obj, nameof (obj)).Handle)
		//     {
		//     }
		//
		internal static T ThrowOnNull<T>(T obj, string name, string? message = null) where T : class
		{
			return obj ?? throw new ArgumentNullException(name, message);
		}


		enum NXByteOrder /* unspecified in header, means most likely int */
		{
			Unknown,
			LittleEndian,
			BigEndian,
		}

		[StructLayout(LayoutKind.Sequential)]
		struct NXArchInfo
		{
			IntPtr name; // const char *
			public int CpuType; // cpu_type_t -> integer_t -> int
			public int CpuSubType; // cpu_subtype_t -> integer_t -> int
			public NXByteOrder ByteOrder;
			IntPtr description; // const char *

			public string Name
			{
				get { return Marshal.PtrToStringUTF8(name)!; }
			}

			public string Description
			{
				get { return Marshal.PtrToStringUTF8(description)!; }
			}
		}

		[DllImport(Constants.libSystemLibrary)]
		static unsafe extern NXArchInfo* NXGetLocalArchInfo();

		public static bool IsARM64CallingConvention;

		[BindingImpl(BindingImplOptions.Optimizable)]
		static bool GetIsARM64CallingConvention()
		{
			if (IntPtr.Size != 8)
				return false;

			unsafe
			{
				return NXGetLocalArchInfo()->Name.StartsWith("arm64");
			}
		}

		// Get the GCHandle from the IntPtr value and get the wrapped object.
		internal static object? GetGCHandleTarget(IntPtr ptr)
		{
			if (ptr == IntPtr.Zero)
				return null;
			return GCHandle.FromIntPtr(ptr).Target;
		}

		// Allocate a GCHandle and return the IntPtr to it.
		internal static IntPtr AllocGCHandle(object? value)
		{
			return GCHandle.ToIntPtr(GCHandle.Alloc(value));
		}

#if __MACCATALYST__
		static string? _iOSSupportVersion;
		internal static string iOSSupportVersion {
			get {
				if (_iOSSupportVersion is null) {
					// This is how Apple does it: https://github.com/llvm/llvm-project/blob/62ec4ac90738a5f2d209ed28c822223e58aaaeb7/lldb/source/Host/macosx/objcxx/HostInfoMacOSX.mm#L100-L105
					using var dict = NSMutableDictionary.FromFile ("/System/Library/CoreServices/SystemVersion.plist");
					using var str = (NSString) "iOSSupportVersion";
					using var obj = dict.ObjectForKey (str);
					_iOSSupportVersion = obj.ToString ();
				}
				return _iOSSupportVersion;
			}
		}
#endif

		// Takes a GCHandle (as an IntPtr) for an exception, frees the GCHandle, and throws the exception.
		// If the IntPtr does not represent a valid GCHandle, then the function just returns.
		// This method must be public, because the generator can generate calls to it (thus third-party binding libraries may need it).
		[EditorBrowsable(EditorBrowsableState.Never)]
		public static void ThrowException(IntPtr gchandle)
		{
			if (gchandle == IntPtr.Zero)
				return;
			var handle = GCHandle.FromIntPtr(gchandle);
			var exc = handle.Target as Exception;
			handle.Free();

			if (exc is null)
				return;

			throw exc;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public static ulong ConvertNativeEnumValueToManaged(nuint value, bool targetTypeHasMaxValue)
		{
#if ARCH_32
			// Check if we got UInt32.MaxValue, which should probably be UInt64.MaxValue
			if (targetTypeHasMaxValue && value == nuint.MaxValue)
				return ulong.MaxValue;
#endif
			return (ulong)value;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public static long ConvertNativeEnumValueToManaged(nint value, bool targetTypeHasMaxValue)
		{
#if ARCH_32
			// Check if we got Int32.MaxValue, which should probably be Int64.MaxValue
			if (targetTypeHasMaxValue && value == nint.MaxValue)
				return long.MaxValue;
#endif
			return (long)value;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public static nint ConvertManagedEnumValueToNative(long value)
		{
			return (nint)value;
		}

		[EditorBrowsable(EditorBrowsableState.Advanced)]
		public static nuint ConvertManagedEnumValueToNative(ulong value)
		{
			return (nuint)value;
		}

#if NET || !MONOMAC // legacy Xamarin.Mac has a different implementation in Runtime.mac.cs
		public static string? OriginalWorkingDirectory
		{
			get
			{
				return Marshal.PtrToStringUTF8(xamarin_get_original_working_directory_path());
			}
		}

		[DllImport("__Internal")]
		static extern IntPtr xamarin_get_original_working_directory_path();
#endif // NET || !__MACOS__

	}

	internal class IntPtrEqualityComparer : IEqualityComparer<IntPtr>
	{
		public bool Equals(IntPtr x, IntPtr y)
		{
			return x == y;
		}
		public int GetHashCode(IntPtr obj)
		{
			return obj.GetHashCode();
		}
	}

	internal class TypeEqualityComparer : IEqualityComparer<Type>
	{
		public bool Equals(Type? x, Type? y)
		{
			return (object?)x == (object?)y;
		}
		public int GetHashCode(Type? obj)
		{
			if (obj is null)
				return 0;
			return obj.GetHashCode();
		}
	}

	internal struct IntPtrTypeValueTuple : IEquatable<IntPtrTypeValueTuple>
	{
		static readonly IEqualityComparer<IntPtr> item1Comparer = Runtime.IntPtrEqualityComparer;
		static readonly IEqualityComparer<Type> item2Comparer = Runtime.TypeEqualityComparer;

		public readonly IntPtr Item1;
		public readonly Type Item2;

		public IntPtrTypeValueTuple(IntPtr item1, Type item2)
		{
			Item1 = item1;
			Item2 = item2;
		}

		public bool Equals(IntPtrTypeValueTuple other)
		{
			return item1Comparer.Equals(Item1, other.Item1) &&
				item2Comparer.Equals(Item2, other.Item2);
		}

		public override bool Equals(object? obj)
		{
			if (obj is IntPtrTypeValueTuple vt)
				return Equals(vt);

			return false;
		}

		public override int GetHashCode()
		{
			return item1Comparer.GetHashCode(Item1) ^ item2Comparer.GetHashCode(Item2);
		}

		public static bool operator ==(IntPtrTypeValueTuple left, IntPtrTypeValueTuple right)
		{
			return left.Equals(right);
		}

		public static bool operator !=(IntPtrTypeValueTuple left, IntPtrTypeValueTuple right)
		{
			return !left.Equals(right);
		}
	}
}