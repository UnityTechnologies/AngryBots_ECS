using System;

namespace NUnit.Framework
{
    public class TestAttribute : Attribute
    {
    }

    public class TestFixtureAttribute : Attribute
    {
    }

    public class SetUpAttribute : Attribute
    {
    }
    
    public class TearDownAttribute : Attribute
    {
    }
    
    public class IgnoreAttribute : Attribute
    {
        public IgnoreAttribute(string msg)
        {
            
        }
    }


    public static class Assert
    {
        public static void IsTrue(bool value)
        {
            
        }

        public static void AreEqual(object p0, object ceilpow2)
        {
            throw new NotImplementedException();
        }

        public static void Fail(string msg)
        {
            
        }

        public static T Throws<T>(Action action) where T : Exception
        {
            throw new NotImplementedException();
        }

        public static void IsFalse(bool hasComponent)
        {
            throw new NotImplementedException();
        }

        public static void AreNotEqual(object i, object value)
        {
            throw new NotImplementedException();
        }

        public static void That(bool b)
        {
            throw new NotImplementedException();
        }
    }
}