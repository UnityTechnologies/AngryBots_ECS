using System;
using System.Runtime.InteropServices;

namespace UTiny
{
    [AttributeUsage(AttributeTargets.Method)]
    public class PureAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.All)]
    public class IgnoreAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.All)]
    public class CppNameAttribute : Attribute
    {
        public string Name;

        public CppNameAttribute(string name)
        {
            Name = name;
        }
    }

    [AttributeUsage(AttributeTargets.All)]
    public class TSNameAttribute : Attribute
    {
        public string Name;

        public TSNameAttribute(string name)
        {
            Name = name;
        }
    }

    [AttributeUsage(AttributeTargets.All)]
    public class CsNameAttribute : Attribute
    {
        public string Name;

        public CsNameAttribute(string name)
        {
            Name = name;
        }
    }

    [AttributeUsage(AttributeTargets.All)]
    public class JSNameAttribute : Attribute
    {
        public string Name;

        public JSNameAttribute(string name)
        {
            Name = name;
        }
    }

    [AttributeUsage(AttributeTargets.All)]
    public class TSExtendsAttribute : Attribute
    {
        public string Name;

        public TSExtendsAttribute(string name)
        {
            Name = name;
        }
    }

    // Completely hide from the editor.  Don't even generate types.
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class HideInEditorAttribute : Attribute
    {
    }

    // Internal type, method, or field -- don't generate in the .d.ts files
    [AttributeUsage(AttributeTargets.All)]
    public class InternalAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
    public class NonSharedPtrAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
    public class SharedPtrAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.All)]
    public class CppCustomImplAttribute : Attribute
    {
    }

    // If this attribute is given to a type or function, there is a pre-existing implementation of the symbol
    // handwritten in C#, so BindGem should not generate a marshalling function to it
    [AttributeUsage(AttributeTargets.All)]
    public class CsCustomImplAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
    public class CsPartialAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Interface)]
    public class ServiceAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Interface)]
    public class PureJSServiceAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.All)]
    public class JSHideAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.All)]
    public class JSCustomImplAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.All)]
    public class ConstructableAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Struct)]
    public class ConfigurationAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class ModuleDependsAttribute : Attribute
    {
        public ModuleDependsAttribute(string mod, string dep)
        {
            ThisModule = mod;
            DepModule = dep;
        }

        public string ThisModule, DepModule;
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class ModuleDescriptionAttribute : Attribute
    {
        public ModuleDescriptionAttribute(string mod, string desc)
        {
            ThisModule = mod;
            Description = desc;
        }

        public string ThisModule, Description;
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class ModuleHideInEditorAttribute : Attribute
    {
        public ModuleHideInEditorAttribute(string mod)
        {
            ThisModule = mod;
        }

        public string ThisModule;
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class ReadOnlyAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class UpdateBeforeAttribute : Attribute
    {
        public UpdateBeforeAttribute(Type otherSystem)
        {
            OtherSystem = otherSystem;
        }

        public Type OtherSystem;
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class UpdateAfterAttribute : Attribute
    {
        public UpdateAfterAttribute(Type otherSystem)
        {
            OtherSystem = otherSystem;
        }

        public Type OtherSystem;
    }

    public enum PlatformImplementation
    {
        HTML,
        Native
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class ImplementationOfAttribute : Attribute
    {
        public ImplementationOfAttribute(PlatformImplementation impl, string module)
        {
            Implementation = impl;
            ParentModule = module;
        }

        public PlatformImplementation Implementation;
        public string ParentModule;
    }

    [Flags]
    public enum Platform
    {
        Web = 1,
        PC = 2,
        WeChat = 4,
        FBInstant = 8,
        iOS = 16,
        Android = 32
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class IncludedPlatformAttribute : Attribute
    {
        public IncludedPlatformAttribute(Platform p)
        {
            Platform = p;
        }

        public Platform Platform;
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class ExcludedPlatformAttribute : Attribute
    {
        public ExcludedPlatformAttribute(Platform p)
        {
            Platform = p;
        }

        public Platform Platform;
    }

    // This interface is used like a tag to define component types that are just POD data on the C++ side
    // (component types that contain strings or arrays are not POD)
    public interface IComponentIsPodData
    {
    }

    public interface IComponentData
    {
    }

    public interface ISharedComponentData
    {
    }

    public interface ISystemStateComponentData
    {
    }

    public interface IBufferElementData
    {
    }

    [JSHide]
    public interface IComponentDataInternal
    {
        [JSHide]
        int NativeComponentId();
        [JSHide]
        int NativeCppComponentSize();
        [JSHide]
        NativeBuffer SerializeToNativeCpp();
        [JSHide]
        void DeserializeFromNativeCpp(global::System.IntPtr src, int size);
    }

    public interface IComponentSystem
    {
    }

    public interface IComponentSystemFence
    {
    }

    public interface IDynamicArray
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    [HideInEditor]
    public struct DynamicArray<T> : IDynamicArray
    {
        public IntPtr mBuffer;
        public int mSize;
        public int mCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    [HideInEditor]
    public struct NativeBuffer
    {
        public IntPtr mBuffer;
        public int mSize;
        public int mCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    [HideInEditor]
    public struct NativeString
    {
        public IntPtr mStr;
        public UInt32 mSize;
    }

    public interface FixedSizeArray<T, Size>
    {
    }

    public struct ComponentTypeId
    {
        public static ComponentTypeId From(int id) {
            ComponentTypeId c;
            c.cid = (uint) id;
            return c;
        }

        public static ComponentTypeId From(uint id) {
            ComponentTypeId c;
            c.cid = id;
            return c;
        }

        public uint cid;
    }
}
