using Mono.Cecil;
using System.Linq;
using System.Reflection;

namespace Unity.Entities.BuildUtils
{
    class TypeHash
    {
        public static ulong FNV1A64(string text)
        {
            // Using http://www.isthe.com/chongo/tech/comp/fnv/index.html#FNV-1a
            // with basis and prime:
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            ulong result = offsetBasis;
            foreach (var c in text)
            {
                result = prime * (result ^ (byte)(c & 255));
                result = prime * (result ^ (byte)(c >> 8));
            }
            return result;
        }

        public static ulong FNV1A64(int val)
        {
            // http://www.isthe.com/chongo/src/fnv/hash_64a.c
            // with basis and prime:
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            ulong result = offsetBasis;
            unchecked
            {
                result = (((ulong)(val & 0x000000FF) >>  0) ^ result) * prime;
                result = (((ulong)(val & 0x0000FF00) >>  8) ^ result) * prime;
                result = (((ulong)(val & 0x00FF0000) >> 16) ^ result) * prime;
                result = (((ulong)(val & 0xFF000000) >> 24) ^ result) * prime;
            }

            return result;
        }

        public static ulong CombineFNV1A64(ulong hash, params ulong[] values)
        {
            const ulong prime = 1099511628211;

            foreach (var value in values)
            {
                hash ^= value;
                hash *= prime;
            }

            return hash;
        }

        public static ulong HashType(TypeDefinition typeDef, int fieldIndex = 0)
        {
            const ulong offsetBasis = 14695981039346656037;
            ulong hash = offsetBasis;

            foreach (var field in typeDef.Fields)
            {
                if (!field.IsStatic)
                {
                    hash = CombineFNV1A64(hash, FNV1A64(field.FullName));
                    if (field.FieldType.IsPrimitive || field.FieldType.IsPointer)
                    {
                        hash = CombineFNV1A64(hash, FNV1A64(fieldIndex));
                        ++fieldIndex;
                    }
                    else if (field.FieldType.IsValueType)
                    {
                        hash = CombineFNV1A64(hash, HashType(field.FieldType.Resolve(), fieldIndex));
                    }
                }
            }

            return hash;
        }

        public static ulong HashVersionAttribute(TypeDefinition typeDef)
        {
            int version = 0;
            if (typeDef.CustomAttributes.Count > 0)
            {
                var versionAttribute = typeDef.CustomAttributes.FirstOrDefault(ca => ca.Constructor.DeclaringType.Name == "TypeVersionAttribute");
                if (versionAttribute != null)
                {
                    version = (int)versionAttribute.ConstructorArguments
                        .First(arg => arg.Type.Name == "Int32")
                        .Value;
                }
            }

            return FNV1A64(version);
        }

        public static ulong CalculateStableTypeHash(TypeDefinition typeDef)
        {
            ulong asmNameHash = FNV1A64(Assembly.CreateQualifiedName(typeDef.Module.Assembly.FullName, typeDef.FullName));
            ulong typeHash = HashType(typeDef);
            ulong versionHash = HashVersionAttribute(typeDef);

            return CombineFNV1A64(asmNameHash, typeHash, versionHash);
        }
    }
}
