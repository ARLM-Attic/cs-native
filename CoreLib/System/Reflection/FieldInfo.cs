////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) Microsoft Corporation.  All rights reserved.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////namespace System.Reflection
namespace System.Reflection
{
    using System;
    using System.Runtime.CompilerServices;

    [Serializable()]
    abstract public class FieldInfo : MemberInfo
    {

        /**
         * The Member type Field.
         */
        public override MemberTypes MemberType
        {
            get
            {
                return System.Reflection.MemberTypes.Field;
            }
        }

        public abstract Type FieldType
        {
            get;
        }

        public abstract object GetValue(object obj);
        
        public virtual void SetValue(Object obj, Object value)
        {
            throw new NotImplementedException();
        }
    }
}

