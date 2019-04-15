using System;

namespace Unity.Entities.StaticTypeRegistry
{
    static internal unsafe class StaticTypeRegistry
    {
#pragma warning disable 0649
        static public readonly Type[] Types;
        static public readonly Type[] Systems;
        static public readonly bool[] SystemIsGroup;
        static public readonly string[] SystemName;    // Debugging. And a reason to have a TinyReflectionSystem
        static public readonly int[] EntityOffsets;
        static public readonly int[] BlobAssetReferenceOffsets;
        static public readonly int[] WriteGroups;
        // This field will be generated in the replacement assembly
        //static public readonly TypeManager.TypeInfo[] TypeInfos;
#pragma warning restore 0649

        public static void RegisterStaticTypes() {
            throw new NotImplementedException("This function should have been replaced by the TypeRegGen build step. Ensure TypeRegGen.exe is generating a new Unity.Entities.StaticTypeRegistry assembly.");
        }

        public static object CreateSystem(Type systemType)
        {
            throw new NotImplementedException("This function should have been replaced by the TypeRegGen build step. Ensure TypeRegGen.exe is generating a new Unity.Entities.StaticTypeRegistry assembly.");
        }

        public static Attribute[] GetSystemAttributes(Type systemType)
        {
            throw new NotImplementedException("This function should have been replaced by the TypeRegGen build step. Ensure TypeRegGen.exe is generating a new Unity.Entities.StaticTypeRegistry assembly.");
        }

        public static bool Equals(void* lhs, void* rhs, int typeIndex)
        {
            // empty -- dynamic reg is used.  TypeRegGen will generate
            // a replacement assembly
            throw new NotImplementedException("This function should have been replaced by the TypeRegGen build step. Ensure TypeRegGen.exe is generating a new Unity.Entities.StaticTypeRegistry assembly.");
        }

        public static bool Equals(object lhs, object rhs, int typeIndex)
        {
            // empty -- dynamic reg is used.  TypeRegGen will generate
            // a replacement assembly
            throw new NotImplementedException("This function should have been replaced by the TypeRegGen build step. Ensure TypeRegGen.exe is generating a new Unity.Entities.StaticTypeRegistry assembly.");
        }

        public static bool Equals(object lhs, void* rhs, int typeIndex)
        {
            // empty -- dynamic reg is used.  TypeRegGen will generate
            // a replacement assembly
            throw new NotImplementedException("This function should have been replaced by the TypeRegGen build step. Ensure TypeRegGen.exe is generating a new Unity.Entities.StaticTypeRegistry assembly.");
        }

        public static int GetHashCode(void* val, int typeIndex)
        {
            // empty -- dynamic reg is used.  TypeRegGen will generate
            // a replacement assembly
            throw new NotImplementedException("This function should have been replaced by the TypeRegGen build step. Ensure TypeRegGen.exe is generating a new Unity.Entities.StaticTypeRegistry assembly.");
        }

        public static int BoxedGetHashCode(object val, int typeIndex)
        {
            // empty -- dynamic reg is used.  TypeRegGen will generate
            // a replacement assembly
            throw new NotImplementedException("This function should have been replaced by the TypeRegGen build step. Ensure TypeRegGen.exe is generating a new Unity.Entities.StaticTypeRegistry assembly.");
        }
    }
}
