using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Microsoft.Android.Sdk.TrimmableTypeMap;

/// <summary>
/// Emits a per-assembly TypeMap PE assembly from a <see cref="TypeMapAssemblyData"/>.
/// This is a mechanical translation — all decision logic lives in <see cref="ModelBuilder"/>.
/// </summary>
/// <remarks>
/// <para>The generated assembly looks like this (pseudo-C#):</para>
/// <code>
/// // Assembly-level TypeMap attributes — one per Java peer type:
/// [assembly: TypeMap&lt;Java.Lang.Object&gt;("android/app/Activity", typeof(Activity_Proxy))]                              // unconditional (ACW)
/// [assembly: TypeMap&lt;Java.Lang.Object&gt;("android/widget/TextView", typeof(TextView_Proxy), typeof(TextView))]          // trimmable (MCW)
/// [assembly: TypeMapAssociation(typeof(MyTextView), typeof(Android_Widget_TextView_Proxy))]                              // alias
///
/// // One proxy type per Java peer that needs activation or UCO wrappers:
/// public sealed class Activity_Proxy : JavaPeerProxy, IAndroidCallableWrapper   // IAndroidCallableWrapper for ACWs only
/// {
///     public Activity_Proxy() : base() { }
///
///     // Creates the managed peer when Java calls into .NET
///     public override IJavaPeerable CreateInstance(IntPtr handle, JniHandleOwnership ownership)
///         =&gt; new Activity(handle, ownership);                        // leaf ctor
///         // or: (Activity)RuntimeHelpers.GetUninitializedObject(typeof(Activity));
///         //     obj.BaseCtor(handle, ownership);                     // inherited ctor
///         // or: new IOnClickListenerInvoker(handle, ownership);      // interface invoker
///         // or: null;                                                // no activation
///         // or: throw new NotSupportedException(...);                // open generic
///
///     public override Type TargetType =&gt; typeof(Activity);
///     public Type InvokerType =&gt; typeof(IOnClickListenerInvoker);    // interfaces only
///
///     // UCO wrappers — [UnmanagedCallersOnly] entry points for JNI native methods (ACWs only):
///     [UnmanagedCallersOnly]
///     public static void n_OnCreate_uco_0(IntPtr jnienv, IntPtr self, IntPtr p0)
///         =&gt; Activity.n_OnCreate(jnienv, self, p0);
///
///     [UnmanagedCallersOnly]
///     public static void nctor_0_uco(IntPtr jnienv, IntPtr self)
///         =&gt; TrimmableNativeRegistration.ActivateInstance(self, typeof(Activity));
///
///     // Registers JNI native methods (ACWs only):
///     public void RegisterNatives(JniType jniType)
///     {
///         JniNativeMethod* methods = stackalloc JniNativeMethod[2];
///         methods[0] = new JniNativeMethod(&amp;__utf8_0, &amp;__utf8_1, &amp;n_OnCreate_uco_0);
///         methods[1] = new JniNativeMethod(&amp;__utf8_2, &amp;__utf8_3, &amp;nctor_0_uco);
///         JniEnvironment.Types.RegisterNatives(jniType.PeerReference, new ReadOnlySpan&lt;JniNativeMethod&gt;(methods, 2));
///     }
/// }
///
/// // Emitted so the proxy assembly can access internal n_* callbacks in the target assembly:
/// [assembly: IgnoresAccessChecksTo("Mono.Android")]
/// </code>
/// </remarks>
sealed class TypeMapAssemblyEmitter
{
	readonly Version _systemRuntimeVersion;

	readonly PEAssemblyBuilder _pe;

	AssemblyReferenceHandle _javaInteropRef;

	TypeReferenceHandle _javaPeerProxyRef;
	TypeReferenceHandle _javaPeerProxyNonGenericRef;
	TypeReferenceHandle _iJavaPeerableRef;
	TypeReferenceHandle _iJavaObjectRef;
	TypeReferenceHandle _jniHandleOwnershipRef;
	TypeReferenceHandle _jniObjectReferenceRef;
	TypeReferenceHandle _jniObjectReferenceOptionsRef;
	TypeReferenceHandle _iAndroidCallableWrapperRef;
	TypeReferenceHandle _jniEnvRef;
	TypeReferenceHandle _javaLangObjectRef;
	TypeReferenceHandle _systemTypeRef;
	TypeReferenceHandle _systemArrayRef;
	TypeReferenceHandle _systemStreamRef;
	TypeReferenceHandle _systemXmlReaderRef;
	TypeReferenceHandle _runtimeTypeHandleRef;
	TypeReferenceHandle _jniTypeRef;
	TypeReferenceHandle _trimmableNativeRegistrationRef;
	TypeReferenceHandle _notSupportedExceptionRef;
	TypeReferenceHandle _runtimeHelpersRef;
	TypeReferenceHandle _inputStreamInvokerRef;
	TypeReferenceHandle _outputStreamInvokerRef;
	TypeReferenceHandle _inputStreamAdapterRef;
	TypeReferenceHandle _outputStreamAdapterRef;
	TypeReferenceHandle _xmlPullParserReaderRef;
	TypeReferenceHandle _xmlResourceParserReaderRef;
	TypeReferenceHandle _xmlReaderPullParserRef;
	TypeReferenceHandle _xmlReaderResourceParserRef;

	MemberReferenceHandle _baseCtorRef;
	MemberReferenceHandle _getTypeFromHandleRef;
	MemberReferenceHandle _getUninitializedObjectRef;
	MemberReferenceHandle _notSupportedExceptionCtorRef;
	MemberReferenceHandle _jniObjectReferenceCtorRef;
	MemberReferenceHandle _jniEnvDeleteRefRef;
	MemberReferenceHandle _jniEnvGetStringRef;
	MemberReferenceHandle _jniEnvGetArrayRef;
	MemberReferenceHandle _jniEnvCopyArrayRef;
	MemberReferenceHandle _jniEnvNewArrayRef;
	MemberReferenceHandle _jniEnvNewStringRef;
	MemberReferenceHandle _jniEnvToLocalJniHandleRef;
	MemberReferenceHandle _javaLangObjectGetObjectRef;
	MemberReferenceHandle _inputStreamInvokerFromJniHandleRef;
	MemberReferenceHandle _outputStreamInvokerFromJniHandleRef;
	MemberReferenceHandle _inputStreamAdapterToLocalJniHandleRef;
	MemberReferenceHandle _outputStreamAdapterToLocalJniHandleRef;
	MemberReferenceHandle _xmlPullParserReaderFromJniHandleRef;
	MemberReferenceHandle _xmlResourceParserReaderFromJniHandleRef;
	MemberReferenceHandle _xmlReaderPullParserToLocalJniHandleRef;
	MemberReferenceHandle _xmlReaderResourceParserToLocalJniHandleRef;
	MemberReferenceHandle _activateInstanceRef;
	MemberReferenceHandle _withinNewObjectScopeRef;
	MemberReferenceHandle _ucoAttrCtorRef;
	BlobHandle _ucoAttrBlobHandle;
	MemberReferenceHandle _typeMapAttrCtorRef2Arg;
	MemberReferenceHandle _typeMapAttrCtorRef3Arg;
	MemberReferenceHandle _typeMapAssociationAttrCtorRef;

	// RegisterNatives with JniNativeMethod
	TypeReferenceHandle _jniNativeMethodRef;
	TypeReferenceHandle _jniEnvironmentRef;
	TypeReferenceHandle _jniEnvironmentTypesRef;
	TypeReferenceHandle _readOnlySpanOpenRef;
	TypeSpecificationHandle _readOnlySpanOfJniNativeMethodSpec;
	MemberReferenceHandle _jniNativeMethodCtorRef;
	MemberReferenceHandle _jniTypePeerReferenceRef;
	MemberReferenceHandle _jniEnvTypesRegisterNativesRef;
	MemberReferenceHandle _readOnlySpanOfJniNativeMethodCtorRef;

	ExportMethodDispatchEmitter? _exportMethodDispatchEmitter;

	/// <summary>
	/// Creates a new emitter.
	/// </summary>
	/// <param name="systemRuntimeVersion">
	/// Version for System.Runtime assembly references.
	/// Will be derived from $(DotNetTargetVersion) MSBuild property in the build task.
	/// </param>
	public TypeMapAssemblyEmitter (Version systemRuntimeVersion)
	{
		_systemRuntimeVersion = systemRuntimeVersion ?? throw new ArgumentNullException (nameof (systemRuntimeVersion));
		_pe = new PEAssemblyBuilder (_systemRuntimeVersion);
	}

	/// <summary>
	/// Emits a PE assembly from the given model and writes it to <paramref name="stream"/>.
	/// </summary>
	public void Emit (TypeMapAssemblyData model, Stream stream)
	{
		if (model is null) {
			throw new ArgumentNullException (nameof (model));
		}
		if (stream is null) {
			throw new ArgumentNullException (nameof (stream));
		}

		EmitCore (model);
		_pe.WritePE (stream);
	}

	void EmitCore (TypeMapAssemblyData model)
	{
		_pe.EmitPreamble (model.AssemblyName, model.ModuleName, MetadataHelper.ComputeContentFingerprint (model));

		_javaInteropRef = _pe.AddAssemblyRef ("Java.Interop", new Version (0, 0, 0, 0));

		EmitTypeReferences ();
		EmitMemberReferences ();
		_exportMethodDispatchEmitter = new ExportMethodDispatchEmitter (_pe, CreateExportMethodDispatchEmitterContext ());

		// Track wrapper method names → handles for RegisterNatives
		var wrapperHandles = new Dictionary<string, MethodDefinitionHandle> ();

		foreach (var proxy in model.ProxyTypes) {
			EmitProxyType (proxy, wrapperHandles);
		}

		foreach (var entry in model.Entries) {
			EmitTypeMapAttribute (entry);
		}

		foreach (var assoc in model.Associations) {
			EmitTypeMapAssociationAttribute (assoc);
		}

		_pe.EmitIgnoresAccessChecksToAttribute (model.IgnoresAccessChecksTo);
	}

	void EmitTypeReferences ()
	{
		var metadata = _pe.Metadata;
		_javaPeerProxyRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Java.Interop"), metadata.GetOrAddString ("JavaPeerProxy`1"));
		_javaPeerProxyNonGenericRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Java.Interop"), metadata.GetOrAddString ("JavaPeerProxy"));
		_iJavaPeerableRef = metadata.AddTypeReference (_javaInteropRef,
			metadata.GetOrAddString ("Java.Interop"), metadata.GetOrAddString ("IJavaPeerable"));
		_iJavaObjectRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("IJavaObject"));
		_jniHandleOwnershipRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("JniHandleOwnership"));
		_jniEnvRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("JNIEnv"));
		_javaLangObjectRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Java.Lang"), metadata.GetOrAddString ("Object"));
		_jniObjectReferenceRef = metadata.AddTypeReference (_javaInteropRef,
			metadata.GetOrAddString ("Java.Interop"), metadata.GetOrAddString ("JniObjectReference"));
		_jniObjectReferenceOptionsRef = metadata.AddTypeReference (_javaInteropRef,
			metadata.GetOrAddString ("Java.Interop"), metadata.GetOrAddString ("JniObjectReferenceOptions"));
		_iAndroidCallableWrapperRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Java.Interop"), metadata.GetOrAddString ("IAndroidCallableWrapper"));
		_systemTypeRef = metadata.AddTypeReference (_pe.SystemRuntimeRef,
			metadata.GetOrAddString ("System"), metadata.GetOrAddString ("Type"));
		_systemArrayRef = metadata.AddTypeReference (_pe.SystemRuntimeRef,
			metadata.GetOrAddString ("System"), metadata.GetOrAddString ("Array"));
		_systemStreamRef = metadata.AddTypeReference (_pe.SystemRuntimeRef,
			metadata.GetOrAddString ("System.IO"), metadata.GetOrAddString ("Stream"));
		var systemXmlRef = _pe.FindOrAddAssemblyRef ("System.Xml.ReaderWriter");
		_systemXmlReaderRef = metadata.AddTypeReference (systemXmlRef,
			metadata.GetOrAddString ("System.Xml"), metadata.GetOrAddString ("XmlReader"));
		_runtimeTypeHandleRef = metadata.AddTypeReference (_pe.SystemRuntimeRef,
			metadata.GetOrAddString ("System"), metadata.GetOrAddString ("RuntimeTypeHandle"));
		_jniTypeRef = metadata.AddTypeReference (_javaInteropRef,
			metadata.GetOrAddString ("Java.Interop"), metadata.GetOrAddString ("JniType"));
		_trimmableNativeRegistrationRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("TrimmableNativeRegistration"));
		_notSupportedExceptionRef = metadata.AddTypeReference (_pe.SystemRuntimeRef,
			metadata.GetOrAddString ("System"), metadata.GetOrAddString ("NotSupportedException"));
		_runtimeHelpersRef = metadata.AddTypeReference (_pe.SystemRuntimeRef,
			metadata.GetOrAddString ("System.Runtime.CompilerServices"), metadata.GetOrAddString ("RuntimeHelpers"));
		_inputStreamInvokerRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("InputStreamInvoker"));
		_outputStreamInvokerRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("OutputStreamInvoker"));
		_inputStreamAdapterRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("InputStreamAdapter"));
		_outputStreamAdapterRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("OutputStreamAdapter"));
		_xmlPullParserReaderRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("XmlPullParserReader"));
		_xmlResourceParserReaderRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("XmlResourceParserReader"));
		_xmlReaderPullParserRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("XmlReaderPullParser"));
		_xmlReaderResourceParserRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Android.Runtime"), metadata.GetOrAddString ("XmlReaderResourceParser"));

		_jniNativeMethodRef = metadata.AddTypeReference (_javaInteropRef,
			metadata.GetOrAddString ("Java.Interop"), metadata.GetOrAddString ("JniNativeMethod"));
		_jniEnvironmentRef = metadata.AddTypeReference (_javaInteropRef,
			metadata.GetOrAddString ("Java.Interop"), metadata.GetOrAddString ("JniEnvironment"));
		_jniEnvironmentTypesRef = metadata.AddTypeReference (_jniEnvironmentRef,
			default, metadata.GetOrAddString ("Types"));

		// ReadOnlySpan<JniNativeMethod> — TypeSpec for generic instantiation
		_readOnlySpanOpenRef = metadata.AddTypeReference (_pe.SystemRuntimeRef,
			metadata.GetOrAddString ("System"), metadata.GetOrAddString ("ReadOnlySpan`1"));
		_readOnlySpanOfJniNativeMethodSpec = MakeGenericTypeSpec_ValueType (_readOnlySpanOpenRef, _jniNativeMethodRef);
	}

	void EmitMemberReferences ()
	{
		_baseCtorRef = _pe.AddMemberRef (_javaPeerProxyRef, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (2,
				rt => rt.Void (),
				p => {
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
				}));

		_getTypeFromHandleRef = _pe.AddMemberRef (_systemTypeRef, "GetTypeFromHandle",
			sig => sig.MethodSignature ().Parameters (1,
				rt => rt.Type ().Type (_systemTypeRef, false),
				p => p.AddParameter ().Type ().Type (_runtimeTypeHandleRef, true)));

		_getUninitializedObjectRef = _pe.AddMemberRef (_runtimeHelpersRef, "GetUninitializedObject",
			sig => sig.MethodSignature ().Parameters (1,
				rt => rt.Type ().Object (),
				p => p.AddParameter ().Type ().Type (_systemTypeRef, false)));

		_notSupportedExceptionCtorRef = _pe.AddMemberRef (_notSupportedExceptionRef, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (1,
				rt => rt.Void (),
				p => p.AddParameter ().Type ().String ()));

		_jniObjectReferenceCtorRef = _pe.AddMemberRef (_jniObjectReferenceRef, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (1,
				rt => rt.Void (),
				p => p.AddParameter ().Type ().IntPtr ()));

		// JNIEnv.DeleteRef(IntPtr, JniHandleOwnership) — static, internal
		// Used by JI-style activation to clean up the original handle after constructing the peer.
		// Matches the legacy TypeManager.CreateProxy behavior.
		_jniEnvDeleteRefRef = _pe.AddMemberRef (_jniEnvRef, "DeleteRef",
			sig => sig.MethodSignature ().Parameters (2,
				rt => rt.Void (),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_jniHandleOwnershipRef, true);
				}));

		_jniEnvGetStringRef = _pe.AddMemberRef (_jniEnvRef, "GetString",
			sig => sig.MethodSignature ().Parameters (2,
				rt => rt.Type ().String (),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_jniHandleOwnershipRef, true);
				}));

		_jniEnvGetArrayRef = _pe.AddMemberRef (_jniEnvRef, "GetArray",
			sig => sig.MethodSignature ().Parameters (3,
				rt => rt.Type ().Type (_systemArrayRef, false),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_jniHandleOwnershipRef, true);
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
				}));

		_jniEnvCopyArrayRef = _pe.AddMemberRef (_jniEnvRef, "CopyArray",
			sig => sig.MethodSignature ().Parameters (3,
				rt => rt.Void (),
				p => {
					p.AddParameter ().Type ().Type (_systemArrayRef, false);
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
					p.AddParameter ().Type ().IntPtr ();
				}));

		_jniEnvNewArrayRef = _pe.AddMemberRef (_jniEnvRef, "NewArray",
			sig => sig.MethodSignature ().Parameters (2,
				rt => rt.Type ().IntPtr (),
				p => {
					p.AddParameter ().Type ().Type (_systemArrayRef, false);
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
				}));

		_jniEnvNewStringRef = _pe.AddMemberRef (_jniEnvRef, "NewString",
			sig => sig.MethodSignature ().Parameters (1,
				rt => rt.Type ().IntPtr (),
				p => p.AddParameter ().Type ().String ()));

		_jniEnvToLocalJniHandleRef = _pe.AddMemberRef (_jniEnvRef, "ToLocalJniHandle",
			sig => sig.MethodSignature ().Parameters (1,
				rt => rt.Type ().IntPtr (),
				p => p.AddParameter ().Type ().Type (_iJavaObjectRef, false)));

		_javaLangObjectGetObjectRef = _pe.AddMemberRef (_javaLangObjectRef, "GetObject",
			sig => sig.MethodSignature ().Parameters (3,
				rt => rt.Type ().Type (_iJavaPeerableRef, false),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_jniHandleOwnershipRef, true);
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
				}));

		_inputStreamInvokerFromJniHandleRef = _pe.AddMemberRef (_inputStreamInvokerRef, "FromJniHandle",
			sig => sig.MethodSignature ().Parameters (2,
				rt => rt.Type ().Type (_systemStreamRef, false),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_jniHandleOwnershipRef, true);
				}));

		_outputStreamInvokerFromJniHandleRef = _pe.AddMemberRef (_outputStreamInvokerRef, "FromJniHandle",
			sig => sig.MethodSignature ().Parameters (2,
				rt => rt.Type ().Type (_systemStreamRef, false),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_jniHandleOwnershipRef, true);
				}));

		_inputStreamAdapterToLocalJniHandleRef = _pe.AddMemberRef (_inputStreamAdapterRef, "ToLocalJniHandle",
			sig => sig.MethodSignature ().Parameters (1,
				rt => rt.Type ().IntPtr (),
				p => p.AddParameter ().Type ().Type (_systemStreamRef, false)));

		_outputStreamAdapterToLocalJniHandleRef = _pe.AddMemberRef (_outputStreamAdapterRef, "ToLocalJniHandle",
			sig => sig.MethodSignature ().Parameters (1,
				rt => rt.Type ().IntPtr (),
				p => p.AddParameter ().Type ().Type (_systemStreamRef, false)));

		_xmlPullParserReaderFromJniHandleRef = _pe.AddMemberRef (_xmlPullParserReaderRef, "FromJniHandle",
			sig => sig.MethodSignature ().Parameters (2,
				rt => rt.Type ().Type (_systemXmlReaderRef, false),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_jniHandleOwnershipRef, true);
				}));

		_xmlResourceParserReaderFromJniHandleRef = _pe.AddMemberRef (_xmlResourceParserReaderRef, "FromJniHandle",
			sig => sig.MethodSignature ().Parameters (2,
				rt => rt.Type ().Type (_systemXmlReaderRef, false),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_jniHandleOwnershipRef, true);
				}));

		_xmlReaderPullParserToLocalJniHandleRef = _pe.AddMemberRef (_xmlReaderPullParserRef, "ToLocalJniHandle",
			sig => sig.MethodSignature ().Parameters (1,
				rt => rt.Type ().IntPtr (),
				p => p.AddParameter ().Type ().Type (_systemXmlReaderRef, false)));

		_xmlReaderResourceParserToLocalJniHandleRef = _pe.AddMemberRef (_xmlReaderResourceParserRef, "ToLocalJniHandle",
			sig => sig.MethodSignature ().Parameters (1,
				rt => rt.Type ().IntPtr (),
				p => p.AddParameter ().Type ().Type (_systemXmlReaderRef, false)));

		_activateInstanceRef = _pe.AddMemberRef (_trimmableNativeRegistrationRef, "ActivateInstance",
			sig => sig.MethodSignature ().Parameters (2,
				rt => rt.Void (),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
				}));

		// JniEnvironment.get_WithinNewObjectScope() -> bool (static property)
		_withinNewObjectScopeRef = _pe.AddMemberRef (_jniEnvironmentRef, "get_WithinNewObjectScope",
			sig => sig.MethodSignature ().Parameters (0,
				rt => rt.Type ().Boolean (),
				p => { }));

		// JniNativeMethod..ctor(byte*, byte*, IntPtr)
		_jniNativeMethodCtorRef = _pe.AddMemberRef (_jniNativeMethodRef, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (3,
				rt => rt.Void (),
				p => {
					p.AddParameter ().Type ().Pointer ().Byte ();
					p.AddParameter ().Type ().Pointer ().Byte ();
					p.AddParameter ().Type ().IntPtr ();
				}));

		// JniType.get_PeerReference() -> JniObjectReference
		_jniTypePeerReferenceRef = _pe.AddMemberRef (_jniTypeRef, "get_PeerReference",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (0,
				rt => rt.Type ().Type (_jniObjectReferenceRef, true),
				p => { }));

		// JniEnvironment.Types.RegisterNatives(JniObjectReference, ReadOnlySpan<JniNativeMethod>)
		_jniEnvTypesRegisterNativesRef = _pe.AddMemberRef (_jniEnvironmentTypesRef, "RegisterNatives",
			sig => sig.MethodSignature ().Parameters (2,
				rt => rt.Void (),
				p => {
					p.AddParameter ().Type ().Type (_jniObjectReferenceRef, true);
					// ReadOnlySpan<JniNativeMethod> — must encode as GENERICINST manually
					EncodeReadOnlySpanOfJniNativeMethod (p.AddParameter ().Type ());
				}));

		// ReadOnlySpan<JniNativeMethod>..ctor(void*, int)
		_readOnlySpanOfJniNativeMethodCtorRef = _pe.AddMemberRef (_readOnlySpanOfJniNativeMethodSpec, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (2,
				rt => rt.Void (),
				p => {
					p.AddParameter ().Type ().VoidPointer ();
					p.AddParameter ().Type ().Int32 ();
				}));

		var ucoAttrTypeRef = _pe.Metadata.AddTypeReference (_pe.SystemRuntimeInteropServicesRef,
			_pe.Metadata.GetOrAddString ("System.Runtime.InteropServices"),
			_pe.Metadata.GetOrAddString ("UnmanagedCallersOnlyAttribute"));
		_ucoAttrCtorRef = _pe.AddMemberRef (ucoAttrTypeRef, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (0, rt => rt.Void (), p => { }));

		// Pre-compute the UCO attribute blob — it's always the same 4 bytes (prolog + no named args)
		_ucoAttrBlobHandle = _pe.BuildAttributeBlob (b => { });

		EmitTypeMapAttributeCtorRef ();
		EmitTypeMapAssociationAttributeCtorRef ();
	}

	void EmitTypeMapAttributeCtorRef ()
	{
		var metadata = _pe.Metadata;
		var typeMapAttrOpenRef = metadata.AddTypeReference (_pe.SystemRuntimeInteropServicesRef,
			metadata.GetOrAddString ("System.Runtime.InteropServices"),
			metadata.GetOrAddString ("TypeMapAttribute`1"));
		var javaLangObjectRef = metadata.AddTypeReference (_pe.MonoAndroidRef,
			metadata.GetOrAddString ("Java.Lang"), metadata.GetOrAddString ("Object"));

		var closedAttrTypeSpec = _pe.MakeGenericTypeSpec (typeMapAttrOpenRef, javaLangObjectRef);

		// 2-arg: TypeMap(string jniName, Type proxyType) — unconditional
		_typeMapAttrCtorRef2Arg = _pe.AddMemberRef (closedAttrTypeSpec, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (2,
				rt => rt.Void (),
				p => {
					p.AddParameter ().Type ().String ();
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
				}));

		// 3-arg: TypeMap(string jniName, Type proxyType, Type targetType) — trimmable
		_typeMapAttrCtorRef3Arg = _pe.AddMemberRef (closedAttrTypeSpec, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (3,
				rt => rt.Void (),
				p => {
					p.AddParameter ().Type ().String ();
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
				}));
	}

	void EmitTypeMapAssociationAttributeCtorRef ()
	{
		var metadata = _pe.Metadata;
		// TypeMapAssociationAttribute is in System.Runtime.InteropServices, takes 2 Type args:
		// TypeMapAssociation(Type sourceType, Type aliasProxyType)
		var typeMapAssociationAttrRef = metadata.AddTypeReference (_pe.SystemRuntimeInteropServicesRef,
			metadata.GetOrAddString ("System.Runtime.InteropServices"),
			metadata.GetOrAddString ("TypeMapAssociationAttribute"));

		_typeMapAssociationAttrCtorRef = _pe.AddMemberRef (typeMapAssociationAttrRef, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (2,
				rt => rt.Void (),
				p => {
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
					p.AddParameter ().Type ().Type (_systemTypeRef, false);
				}));
	}

	ExportMethodDispatchEmitterContext CreateExportMethodDispatchEmitterContext ()
	{
		return new ExportMethodDispatchEmitterContext {
			GetTypeFromHandleRef = _getTypeFromHandleRef,
			JniObjectReferenceRef = _jniObjectReferenceRef,
			IJavaObjectRef = _iJavaObjectRef,
			JniTypeRef = _jniTypeRef,
			JniNativeMethodRef = _jniNativeMethodRef,
			ReadOnlySpanOpenRef = _readOnlySpanOpenRef,
			JniEnvGetStringRef = _jniEnvGetStringRef,
			JniEnvGetArrayRef = _jniEnvGetArrayRef,
			JniEnvCopyArrayRef = _jniEnvCopyArrayRef,
			JniEnvNewArrayRef = _jniEnvNewArrayRef,
			JniEnvNewStringRef = _jniEnvNewStringRef,
			JniEnvToLocalJniHandleRef = _jniEnvToLocalJniHandleRef,
			JavaLangObjectGetObjectRef = _javaLangObjectGetObjectRef,
			InputStreamInvokerFromJniHandleRef = _inputStreamInvokerFromJniHandleRef,
			OutputStreamInvokerFromJniHandleRef = _outputStreamInvokerFromJniHandleRef,
			InputStreamAdapterToLocalJniHandleRef = _inputStreamAdapterToLocalJniHandleRef,
			OutputStreamAdapterToLocalJniHandleRef = _outputStreamAdapterToLocalJniHandleRef,
			XmlPullParserReaderFromJniHandleRef = _xmlPullParserReaderFromJniHandleRef,
			XmlResourceParserReaderFromJniHandleRef = _xmlResourceParserReaderFromJniHandleRef,
			XmlReaderPullParserToLocalJniHandleRef = _xmlReaderPullParserToLocalJniHandleRef,
			XmlReaderResourceParserToLocalJniHandleRef = _xmlReaderResourceParserToLocalJniHandleRef,
			ActivateInstanceRef = _activateInstanceRef,
			UcoAttrCtorRef = _ucoAttrCtorRef,
			UcoAttrBlobHandle = _ucoAttrBlobHandle,
			JniNativeMethodCtorRef = _jniNativeMethodCtorRef,
			JniTypePeerReferenceRef = _jniTypePeerReferenceRef,
			JniEnvTypesRegisterNativesRef = _jniEnvTypesRegisterNativesRef,
			ReadOnlySpanOfJniNativeMethodCtorRef = _readOnlySpanOfJniNativeMethodCtorRef,
		};
	}

	ExportMethodDispatchEmitter GetExportMethodDispatchEmitter ()
	{
		if (_exportMethodDispatchEmitter == null) {
			throw new InvalidOperationException ("ExportMethodDispatchEmitter has not been initialized.");
		}

		return _exportMethodDispatchEmitter;
	}

	void EmitProxyType (JavaPeerProxyData proxy, Dictionary<string, MethodDefinitionHandle> wrapperHandles)
	{
		var exportMethodDispatchEmitter = GetExportMethodDispatchEmitter ();

		if (proxy.IsAcw) {
			// RegisterNatives uses RVA-backed UTF-8 fields under <PrivateImplementationDetails>.
			// Materialize those helper types before adding the proxy TypeDef, otherwise the
			// later RegisterNatives method can be attached to the helper type instead.
			foreach (var reg in proxy.NativeRegistrations) {
				_pe.GetOrAddUtf8Field (reg.JniMethodName);
				_pe.GetOrAddUtf8Field (reg.JniSignature);
			}
		}

		var metadata = _pe.Metadata;
		var targetTypeRef = _pe.ResolveTypeRef (proxy.TargetType);

		// Open generic definitions derive from the non-generic `JavaPeerProxy` abstract base.
		// Using `JavaPeerProxy<T>` with an open T would force the CLR to resolve a generic
		// argument that isn't available via the TypeMapLazyDictionary loader, and using a
		// placeholder like `Java.Lang.Object` leaks an incorrect TargetType into the typemap.
		// The non-generic base takes `targetType` as a ctor parameter, so we can pass the real
		// open-generic type token (a TypeRef, not a closed TypeSpec) and keep TargetType correct.
		EntityHandle proxyBaseType;
		MemberReferenceHandle baseCtorRef;
		if (proxy.IsGenericDefinition) {
			proxyBaseType = _javaPeerProxyNonGenericRef;
			baseCtorRef = _pe.AddMemberRef (_javaPeerProxyNonGenericRef, ".ctor",
				sig => sig.MethodSignature (isInstanceMethod: true).Parameters (3,
					rt => rt.Void (),
					p => {
						p.AddParameter ().Type ().String ();
						p.AddParameter ().Type ().Type (_systemTypeRef, false);
						p.AddParameter ().Type ().Type (_systemTypeRef, false);
					}));
		} else {
			var genericProxyBase = _pe.MakeGenericTypeSpec (_javaPeerProxyRef, targetTypeRef);
			proxyBaseType = genericProxyBase;
			baseCtorRef = _pe.AddMemberRef (genericProxyBase, ".ctor",
				sig => sig.MethodSignature (isInstanceMethod: true).Parameters (2,
					rt => rt.Void (),
					p => {
						p.AddParameter ().Type ().String ();
						p.AddParameter ().Type ().Type (_systemTypeRef, false);
					}));
		}
		var typeDefHandle = metadata.AddTypeDefinition (
			TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
			metadata.GetOrAddString (proxy.Namespace),
			metadata.GetOrAddString (proxy.TypeName),
			proxyBaseType,
			MetadataTokens.FieldDefinitionHandle (metadata.GetRowCount (TableIndex.Field) + 1),
			MetadataTokens.MethodDefinitionHandle (metadata.GetRowCount (TableIndex.MethodDef) + 1));

		if (proxy.IsAcw) {
			metadata.AddInterfaceImplementation (typeDefHandle, _iAndroidCallableWrapperRef);
		}

		// Self-apply: the proxy type is its own [JavaPeerProxy] attribute.
		// This enables type.GetCustomAttribute<JavaPeerProxy>() to instantiate the proxy
		// at runtime for AOT-safe type resolution.
		var selfAttrCtorRef = _pe.AddMemberRef (typeDefHandle, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (0, rt => rt.Void (), p => { }));
		var selfAttrBlob = _pe.BuildAttributeBlob (b => { });
		metadata.AddCustomAttribute (typeDefHandle, selfAttrCtorRef, selfAttrBlob);

		// .ctor — pass the resolved JNI name, (for generic-definition base) target type, and
		// optional invoker type to the base proxy constructor.
		_pe.EmitBody (".ctor",
			MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (0, rt => rt.Void (), p => { }),
			encoder => {
				encoder.OpCode (ILOpCode.Ldarg_0);
				encoder.LoadString (metadata.GetOrAddUserString (proxy.JniName));
				if (proxy.IsGenericDefinition) {
					// Non-generic base ctor signature: (string, Type, Type?). Push the open-generic
					// target type as the second argument.
					encoder.OpCode (ILOpCode.Ldtoken);
					encoder.Token (targetTypeRef);
					encoder.Call (_getTypeFromHandleRef);
				}
				if (proxy.InvokerType != null) {
					encoder.OpCode (ILOpCode.Ldtoken);
					encoder.Token (_pe.ResolveTypeRef (proxy.InvokerType));
					encoder.Call (_getTypeFromHandleRef);
				} else {
					encoder.OpCode (ILOpCode.Ldnull);
				}
				encoder.Call (baseCtorRef);
				encoder.OpCode (ILOpCode.Ret);
			});

		// CreateInstance
		EmitCreateInstance (proxy);

		// UCO wrappers
		foreach (var uco in proxy.UcoMethods) {
			var handle = exportMethodDispatchEmitter.EmitUcoMethod (uco);
			wrapperHandles [uco.WrapperName] = handle;
		}

		foreach (var uco in proxy.UcoConstructors) {
			var handle = EmitUcoConstructor (uco, proxy);
			wrapperHandles [uco.WrapperName] = handle;
		}

		// RegisterNatives
		if (proxy.IsAcw) {
			exportMethodDispatchEmitter.EmitRegisterNatives (proxy.NativeRegistrations, wrapperHandles);
		}
	}

	void EmitCreateInstance (JavaPeerProxyData proxy)
	{
		if (!proxy.HasActivation) {
			EmitCreateInstanceNoActivation ();
			return;
		}

		if (proxy.IsGenericDefinition) {
			EmitCreateInstanceGenericDefinition ();
			return;
		}

		// JavaInterop-style activation ctors (ref JniObjectReference, JniObjectReferenceOptions)
		// require parameter conversion from (IntPtr, JniHandleOwnership).
		if (proxy.ActivationCtor?.Style == ActivationCtorStyle.JavaInterop) {
			if (proxy.InvokerType != null) {
				EmitCreateInstanceViaJavaInteropNewobj (_pe.ResolveTypeRef (proxy.InvokerType));
			} else {
				var targetRef = _pe.ResolveTypeRef (proxy.TargetType);
				var jiCtor = proxy.ActivationCtor ?? throw new InvalidOperationException ("ActivationCtor should not be null");
				if (jiCtor.IsOnLeafType) {
					EmitCreateInstanceViaJavaInteropNewobj (targetRef);
				} else {
					EmitCreateInstanceInheritedJavaInteropCtor (targetRef, jiCtor);
				}
			}
			return;
		}

		if (proxy.InvokerType != null) {
			EmitCreateInstanceViaNewobj (_pe.ResolveTypeRef (proxy.InvokerType));
			return;
		}

		// At this point, ActivationCtor is guaranteed non-null (HasActivation && InvokerType == null)
		var activationCtor = proxy.ActivationCtor ?? throw new InvalidOperationException ("ActivationCtor should not be null when HasActivation is true and InvokerType is null");
		var targetTypeRef = _pe.ResolveTypeRef (proxy.TargetType);

		if (activationCtor.IsOnLeafType) {
			EmitCreateInstanceViaNewobj (targetTypeRef);
		} else {
			EmitCreateInstanceInheritedCtor (targetTypeRef, activationCtor);
		}
	}

	void EmitCreateInstanceNoActivation ()
	{
		EmitCreateInstanceBody (encoder => {
			encoder.OpCode (ILOpCode.Ldnull);
			encoder.OpCode (ILOpCode.Ret);
		});
	}

	void EmitCreateInstanceGenericDefinition ()
	{
		EmitCreateInstanceBody (encoder => {
			encoder.LoadString (_pe.Metadata.GetOrAddUserString ("Cannot create instance of open generic type."));
			encoder.OpCode (ILOpCode.Newobj);
			encoder.Token (_notSupportedExceptionCtorRef);
			encoder.OpCode (ILOpCode.Throw);
		});
	}

	void EmitCreateInstanceViaNewobj (EntityHandle typeRef)
	{
		var ctorRef = AddActivationCtorRef (typeRef);
		EmitCreateInstanceBody (encoder => {
			encoder.OpCode (ILOpCode.Ldarg_1);
			encoder.OpCode (ILOpCode.Ldarg_2);
			encoder.OpCode (ILOpCode.Newobj);
			encoder.Token (ctorRef);
			encoder.OpCode (ILOpCode.Ret);
		});
	}

	void EmitCreateInstanceInheritedCtor (EntityHandle targetTypeRef, ActivationCtorData activationCtor)
	{
		var baseActivationCtorRef = AddActivationCtorRef (_pe.ResolveTypeRef (activationCtor.DeclaringType));
		EmitCreateInstanceBody (encoder => {
			encoder.OpCode (ILOpCode.Ldtoken);
			encoder.Token (targetTypeRef);
			encoder.Call (_getTypeFromHandleRef);
			encoder.Call (_getUninitializedObjectRef);
			encoder.OpCode (ILOpCode.Castclass);
			encoder.Token (targetTypeRef);

			encoder.OpCode (ILOpCode.Dup);
			encoder.OpCode (ILOpCode.Ldarg_1);
			encoder.OpCode (ILOpCode.Ldarg_2);
			encoder.Call (baseActivationCtorRef);

			encoder.OpCode (ILOpCode.Ret);
		});
	}

	/// <summary>
	/// Emits CreateInstance for JavaInterop-style activation (leaf type):
	///   var jniRef = new JniObjectReference(handle);
	///   var result = new TargetType(ref jniRef, JniObjectReferenceOptions.Copy);
	///   JNIEnv.DeleteRef(handle, ownership);
	///   return result;
	/// </summary>
	void EmitCreateInstanceViaJavaInteropNewobj (EntityHandle typeRef)
	{
		var ctorRef = AddJavaInteropActivationCtorRef (typeRef);
		EmitCreateInstanceBodyWithLocals (
			EncodeJniObjectReferenceAndObjectLocals,
			encoder => {
				// var jniRef = new JniObjectReference(handle);
				encoder.LoadLocalAddress (0);
				encoder.OpCode (ILOpCode.Ldarg_1); // handle
				encoder.Call (_jniObjectReferenceCtorRef);

				// var result = new TargetType(ref jniRef, JniObjectReferenceOptions.Copy);
				encoder.LoadLocalAddress (0);
				encoder.LoadConstantI4 (1); // JniObjectReferenceOptions.Copy
				encoder.OpCode (ILOpCode.Newobj);
				encoder.Token (ctorRef);
				encoder.StoreLocal (1); // save result

				// JNIEnv.DeleteRef(handle, ownership);
				encoder.OpCode (ILOpCode.Ldarg_1); // handle
				encoder.OpCode (ILOpCode.Ldarg_2); // ownership
				encoder.Call (_jniEnvDeleteRefRef);

				encoder.LoadLocal (1); // load result
				encoder.OpCode (ILOpCode.Ret);
			});
	}

	/// <summary>
	/// Emits CreateInstance for JavaInterop-style activation (inherited ctor):
	///   var obj = (TargetType)RuntimeHelpers.GetUninitializedObject(typeof(TargetType));
	///   var jniRef = new JniObjectReference(handle);
	///   obj.BaseCtor(ref jniRef, JniObjectReferenceOptions.Copy);
	///   JNIEnv.DeleteRef(handle, ownership);
	///   return obj;
	/// </summary>
	void EmitCreateInstanceInheritedJavaInteropCtor (EntityHandle targetTypeRef, ActivationCtorData activationCtor)
	{
		var baseCtorRef = AddJavaInteropActivationCtorRef (_pe.ResolveTypeRef (activationCtor.DeclaringType));
		EmitCreateInstanceBodyWithLocals (
			EncodeJniObjectReferenceLocal,
			encoder => {
				// var obj = (TargetType)RuntimeHelpers.GetUninitializedObject(typeof(TargetType));
				encoder.OpCode (ILOpCode.Ldtoken);
				encoder.Token (targetTypeRef);
				encoder.Call (_getTypeFromHandleRef);
				encoder.Call (_getUninitializedObjectRef);
				encoder.OpCode (ILOpCode.Castclass);
				encoder.Token (targetTypeRef);

				// dup obj (one copy for the call, one for the return)
				encoder.OpCode (ILOpCode.Dup);

				// var jniRef = new JniObjectReference(handle);
				encoder.LoadLocalAddress (0);
				encoder.OpCode (ILOpCode.Ldarg_1); // handle
				encoder.Call (_jniObjectReferenceCtorRef);

				// obj.BaseCtor(ref jniRef, JniObjectReferenceOptions.Copy);
				encoder.LoadLocalAddress (0);
				encoder.LoadConstantI4 (1); // JniObjectReferenceOptions.Copy
				encoder.Call (baseCtorRef);

				// JNIEnv.DeleteRef(handle, ownership);
				encoder.OpCode (ILOpCode.Ldarg_1); // handle
				encoder.OpCode (ILOpCode.Ldarg_2); // ownership
				encoder.Call (_jniEnvDeleteRefRef);

				encoder.OpCode (ILOpCode.Ret);
			});
	}

	void EncodeJniObjectReferenceLocal (BlobBuilder blob)
	{
		// LOCAL_SIG header (0x07), count = 1, ELEMENT_TYPE_VALUETYPE + compressed token
		blob.WriteByte (0x07); // LOCAL_SIG
		blob.WriteCompressedInteger (1); // 1 local variable
		blob.WriteByte (0x11); // ELEMENT_TYPE_VALUETYPE
		blob.WriteCompressedInteger (CodedIndex.TypeDefOrRefOrSpec (_jniObjectReferenceRef));
	}

	void EncodeJniObjectReferenceAndObjectLocals (BlobBuilder blob)
	{
		// LOCAL_SIG header (0x07), count = 2:
		//   local 0: JniObjectReference (valuetype)
		//   local 1: object (for storing the newobj result across the DeleteRef call)
		blob.WriteByte (0x07); // LOCAL_SIG
		blob.WriteCompressedInteger (2); // 2 local variables
		blob.WriteByte (0x11); // ELEMENT_TYPE_VALUETYPE
		blob.WriteCompressedInteger (CodedIndex.TypeDefOrRefOrSpec (_jniObjectReferenceRef));
		blob.WriteByte (0x1c); // ELEMENT_TYPE_OBJECT
	}

	MemberReferenceHandle AddJavaInteropActivationCtorRef (EntityHandle declaringTypeRef)
	{
		return _pe.AddMemberRef (declaringTypeRef, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (2,
				rt => rt.Void (),
				p => {
					// ref JniObjectReference — encoded as byref valuetype
					p.AddParameter ().Type (isByRef: true).Type (_jniObjectReferenceRef, true);
					// JniObjectReferenceOptions — encoded as valuetype (enum)
					p.AddParameter ().Type ().Type (_jniObjectReferenceOptionsRef, true);
				}));
	}

	void EmitCreateInstanceBody (Action<InstructionEncoder> emitIL)
	{
		_pe.EmitBody ("CreateInstance",
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (2,
				rt => rt.Type ().Type (_iJavaPeerableRef, false),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_jniHandleOwnershipRef, true);
				}),
			emitIL);
	}

	void EmitCreateInstanceBodyWithLocals (Action<BlobBuilder> encodeLocals, Action<InstructionEncoder> emitIL)
	{
		_pe.EmitBody ("CreateInstance",
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (2,
				rt => rt.Type ().Type (_iJavaPeerableRef, false),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_jniHandleOwnershipRef, true);
				}),
			emitIL,
			encodeLocals);
	}

	MemberReferenceHandle AddActivationCtorRef (EntityHandle declaringTypeRef)
	{
		return _pe.AddMemberRef (declaringTypeRef, ".ctor",
			sig => sig.MethodSignature (isInstanceMethod: true).Parameters (2,
				rt => rt.Void (),
				p => {
					p.AddParameter ().Type ().IntPtr ();
					p.AddParameter ().Type ().Type (_jniHandleOwnershipRef, true);
				}));
	}

	MethodDefinitionHandle EmitUcoConstructor (UcoConstructorData uco, JavaPeerProxyData proxy)
	{
		var targetTypeRef = _pe.ResolveTypeRef (uco.TargetType);
		var activationCtor = proxy.ActivationCtor ?? throw new InvalidOperationException (
			$"UCO constructor wrapper requires an activation ctor for '{uco.TargetType.ManagedTypeName}'");

		var jniParams = JniSignatureHelper.ParseParameterTypes (uco.JniSignature);
		int paramCount = 2 + jniParams.Count;

		Action<BlobEncoder> encodeSig = sig => sig.MethodSignature ().Parameters (paramCount,
			rt => rt.Void (),
			p => {
				p.AddParameter ().Type ().IntPtr ();
				p.AddParameter ().Type ().IntPtr ();
				for (int j = 0; j < jniParams.Count; j++) {
					JniSignatureHelper.EncodeClrType (p.AddParameter ().Type (), jniParams [j]);
				}
			});

		if (proxy.IsGenericDefinition || proxy.CannotRegisterInStaticConstructor) {
			var noopHandle = _pe.EmitBody (uco.WrapperName,
				MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
				encodeSig,
				encoder => {
					encoder.OpCode (ILOpCode.Ret);
				});
			_pe.Metadata.AddCustomAttribute (noopHandle, _ucoAttrCtorRef, _ucoAttrBlobHandle);
			return noopHandle;
		}

		MethodDefinitionHandle handle;
		if (activationCtor.Style == ActivationCtorStyle.JavaInterop) {
			var ctorRef = AddJavaInteropActivationCtorRef (
				activationCtor.IsOnLeafType ? targetTypeRef : _pe.ResolveTypeRef (activationCtor.DeclaringType));

			handle = _pe.EmitBody (uco.WrapperName,
				MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
				encodeSig,
				encoder => {
					var skipLabel = encoder.DefineLabel ();
					encoder.Call (_withinNewObjectScopeRef);
					encoder.Branch (ILOpCode.Brtrue, skipLabel);

					if (!activationCtor.IsOnLeafType) {
						encoder.OpCode (ILOpCode.Ldtoken);
						encoder.Token (targetTypeRef);
						encoder.Call (_getTypeFromHandleRef);
						encoder.Call (_getUninitializedObjectRef);
						encoder.OpCode (ILOpCode.Castclass);
						encoder.Token (targetTypeRef);
					}

					encoder.LoadLocalAddress (0);
					encoder.LoadArgument (1);
					encoder.Call (_jniObjectReferenceCtorRef);

					if (activationCtor.IsOnLeafType) {
						encoder.LoadLocalAddress (0);
						encoder.LoadConstantI4 (1);
						encoder.OpCode (ILOpCode.Newobj);
						encoder.Token (ctorRef);
						encoder.OpCode (ILOpCode.Pop);
					} else {
						encoder.LoadLocalAddress (0);
						encoder.LoadConstantI4 (1);
						encoder.Call (ctorRef);
					}

					encoder.MarkLabel (skipLabel);
					encoder.OpCode (ILOpCode.Ret);
				},
				EncodeJniObjectReferenceLocal,
				useBranches: true);
		} else {
			var ctorRef = AddActivationCtorRef (
				activationCtor.IsOnLeafType ? targetTypeRef : _pe.ResolveTypeRef (activationCtor.DeclaringType));

			handle = _pe.EmitBody (uco.WrapperName,
				MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
				encodeSig,
				encoder => {
					var skipLabel = encoder.DefineLabel ();
					encoder.Call (_withinNewObjectScopeRef);
					encoder.Branch (ILOpCode.Brtrue, skipLabel);

					if (activationCtor.IsOnLeafType) {
						encoder.LoadArgument (1);
						encoder.LoadConstantI4 (0);
						encoder.OpCode (ILOpCode.Newobj);
						encoder.Token (ctorRef);
						encoder.OpCode (ILOpCode.Pop);
					} else {
						encoder.OpCode (ILOpCode.Ldtoken);
						encoder.Token (targetTypeRef);
						encoder.Call (_getTypeFromHandleRef);
						encoder.Call (_getUninitializedObjectRef);
						encoder.OpCode (ILOpCode.Castclass);
						encoder.Token (targetTypeRef);

						encoder.LoadArgument (1);
						encoder.LoadConstantI4 (0);
						encoder.Call (ctorRef);
					}

					encoder.MarkLabel (skipLabel);
					encoder.OpCode (ILOpCode.Ret);
				},
				encodeLocals: null,
				useBranches: true);
		}

		_pe.Metadata.AddCustomAttribute (handle, _ucoAttrCtorRef, _ucoAttrBlobHandle);
		return handle;
	}

	void EmitTypeMapAttribute (TypeMapAttributeData entry)
	{
		var ctorRef = entry.IsUnconditional ? _typeMapAttrCtorRef2Arg : _typeMapAttrCtorRef3Arg;
		var blob = _pe.BuildAttributeBlob (b => {
			b.WriteSerializedString (entry.JniName);
			b.WriteSerializedString (entry.ProxyTypeReference);
			if (!entry.IsUnconditional) {
				if (entry.TargetTypeReference is null) {
					throw new InvalidOperationException ($"TargetTypeReference must not be null for conditional entry '{entry.JniName}'");
				}
				b.WriteSerializedString (entry.TargetTypeReference);
			}
		});
		_pe.Metadata.AddCustomAttribute (EntityHandle.AssemblyDefinition, ctorRef, blob);
	}

	void EmitTypeMapAssociationAttribute (TypeMapAssociationData assoc)
	{
		var blob = _pe.BuildAttributeBlob (b => {
			b.WriteSerializedString (assoc.SourceTypeReference);
			b.WriteSerializedString (assoc.AliasProxyTypeReference);
		});
		_pe.Metadata.AddCustomAttribute (EntityHandle.AssemblyDefinition, _typeMapAssociationAttrCtorRef, blob);
	}

	/// <summary>
	/// Writes the ECMA-335 blob for a closed generic value type with a single value-type argument.
	/// E.g., <c>ReadOnlySpan&lt;JniNativeMethod&gt;</c>.
	/// </summary>
	static void EncodeGenericValueTypeInst (BlobBuilder builder, EntityHandle openType, EntityHandle valueTypeArg)
	{
		builder.WriteByte (0x15); // ELEMENT_TYPE_GENERICINST
		builder.WriteByte (0x11); // ELEMENT_TYPE_VALUETYPE
		builder.WriteCompressedInteger (CodedIndex.TypeDefOrRefOrSpec (openType));
		builder.WriteCompressedInteger (1); // generic arity = 1
		builder.WriteByte (0x11); // ELEMENT_TYPE_VALUETYPE
		builder.WriteCompressedInteger (CodedIndex.TypeDefOrRefOrSpec (valueTypeArg));
	}

	/// <summary>
	/// Builds a <c>TypeSpec</c> for a closed generic type with a single value-type argument.
	/// E.g., <c>ReadOnlySpan&lt;JniNativeMethod&gt;</c>.
	/// </summary>
	TypeSpecificationHandle MakeGenericTypeSpec_ValueType (EntityHandle openType, EntityHandle valueTypeArg)
	{
		var sigBlob = new BlobBuilder (32);
		EncodeGenericValueTypeInst (sigBlob, openType, valueTypeArg);
		return _pe.Metadata.AddTypeSpecification (_pe.Metadata.GetOrAddBlob (sigBlob));
	}

	/// <summary>
	/// Encodes <c>ReadOnlySpan&lt;JniNativeMethod&gt;</c> directly into a signature type encoder.
	/// Required because <see cref="SignatureTypeEncoder.Type"/> doesn't accept TypeSpec handles.
	/// </summary>
	void EncodeReadOnlySpanOfJniNativeMethod (SignatureTypeEncoder encoder)
	{
		EncodeGenericValueTypeInst (encoder.Builder, _readOnlySpanOpenRef, _jniNativeMethodRef);
	}
}
