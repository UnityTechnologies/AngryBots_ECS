using ExtensionMethods;
using Mono.Cecil;
using Unity.Entities.BuildUtils;

namespace BindGem
{
    public enum JSSpecialType
    {
        None,
        Vector2,
        Vector3,
        Vector4,
        Quaternion,
        Matrix3,
        Matrix4
    }

    internal static class RoslynExtensionMethods
    {

        internal static TypeDefinition FixedSpecialType(this TypeDefinition type)
        {
            if (type.MetadataType == MetadataType.IntPtr) return type.Module.TypeSystem.IntPtr.Resolve();
            if (type.IsComponentTypeId()) return type.Module.TypeSystem.UInt32.Resolve();
            if (type.IsEntityIdType()) return type.Module.TypeSystem.IntPtr.Resolve();
            if (type.MetadataType == MetadataType.Byte) return type;
            if (type.MetadataType == MetadataType.SByte) return type;
            if (type.MetadataType == MetadataType.Int16) return type;
            if (type.MetadataType == MetadataType.UInt16) return type;
            if (type.MetadataType == MetadataType.Char) return type;
            if (type.MetadataType == MetadataType.Int32) return type;
            if (type.MetadataType == MetadataType.UInt32) return type;
            if (type.MetadataType == MetadataType.Int64) return type;
            if (type.MetadataType == MetadataType.UInt64) return type;
            if (type.MetadataType == MetadataType.Boolean) return type;
            if (type.MetadataType == MetadataType.Double) return type;
            if (type.MetadataType == MetadataType.Single) return type;
            if (type.MetadataType == MetadataType.String) return type;
            return null;
        }

        internal static bool IsComponentTypeId(this TypeReference type)
        {
            return type.Resolve().IsValueType && type.FullName == "UTiny.ComponentTypeId";
        }

        internal static bool IsEntityIdType(this TypeReference type)
        {
            return type.Resolve().IsValueType && type.FullName == "UTiny.EntityId";
        }

        internal static bool IsStructValueType(this TypeReference type)
        {
            if (!type.IsValueType)
                return false;
            if (type.Resolve().IsEnum || type.IsPrimitive)
                return false;
            if (type.IsDynamicArray())
                return false;
            if (type.FixedSpecialType() != null)
                return false;
            if (type.IsEntityIdType() || type.IsComponentTypeId() || type.MetadataType == MetadataType.IntPtr)
                return false;
            return true;
        }

        internal static JSSpecialType JavaScriptSpecialType(this TypeReference typeRef)
        {
            TypeDefinition type = typeRef.Resolve();
            if (!type.HasAttribute("JSCustomImpl"))
                return JSSpecialType.None;

            switch (type.Name)
            {
                case "Vector2": return JSSpecialType.Vector2;
                case "Vector3": return JSSpecialType.Vector3;
                case "Vector4": return JSSpecialType.Vector4;
                case "Quaternion": return JSSpecialType.Quaternion;
                case "Matrix3x3": return JSSpecialType.Matrix3;
                case "Matrix4x4": return JSSpecialType.Matrix4;
                case null: return JSSpecialType.None;
            }

            BindGem.FatalError($"Unknown JSCustomImpl '{type.Name}'");//, type.Locations[0]);
            return JSSpecialType.None;
        }

        internal static string DefaultValueToJSString(this ParameterDefinition param)
        {
            if (param.ParameterType.Resolve().MetadataType == MetadataType.Boolean)
                return param.Constant.ToString().ToLower();
            return param.Constant != null ? param.Constant.ToString() : null;
        }
    }
}
