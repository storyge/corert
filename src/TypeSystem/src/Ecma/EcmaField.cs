﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

using Internal.TypeSystem;

using Interlocked = System.Threading.Interlocked;

namespace Internal.TypeSystem.Ecma
{
    public sealed class EcmaField : FieldDesc
    {
        static class FieldFlags
        {
            public const int BasicMetadataCache     = 0x0001;
            public const int Static                 = 0x0002;
            public const int InitOnly               = 0x0004;
            public const int Literal                = 0x0008;

            public const int AttributeMetadataCache = 0x0100;
            public const int ThreadStatic           = 0x0200;
        };

        EcmaType _type;
        FieldDefinitionHandle _handle;

        TypeDesc _fieldType;
        volatile int _fieldFlags;

        internal EcmaField(EcmaType type, FieldDefinitionHandle handle)
        {
            _type = type;
            _handle = handle;
        }

        public override TypeSystemContext Context
        {
            get
            {
                return _type.Module.Context;
            }
        }

        public override MetadataType OwningType
        {
            get
            {
                return _type;
            }
        }

        public EcmaModule Module
        {
            get
            {
                return _type.Module;
            }
        }

        public MetadataReader MetadataReader
        {
            get
            {
                return _type.MetadataReader;
            }
        }

        public FieldDefinitionHandle Handle
        {
            get
            {
                return _handle;
            }
        }
        
        public FieldDefinition FieldDefinition
        {
            get
            {
                return this.MetadataReader.GetFieldDefinition(_handle);
            }
        }

        void ComputeFieldType()
        {
            var metadataReader = this.Module.MetadataReader;
            BlobReader signatureReader = metadataReader.GetBlobReader(metadataReader.GetFieldDefinition(_handle).Signature);

            EcmaSignatureParser parser = new EcmaSignatureParser(this.Module, signatureReader);
            _fieldType = parser.ParseFieldSignature();
        }

        public override TypeDesc FieldType
        {
            get
            {
                if (_fieldType == null)
                    ComputeFieldType();
                return _fieldType;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int InitializeFieldFlags(int mask)
        {
            int flags = 0;

            if ((mask & FieldFlags.BasicMetadataCache) != 0)
            {
                var fieldDefinition = this.MetadataReader.GetFieldDefinition(_handle);

                var fieldAttributes = fieldDefinition.Attributes;
                if ((fieldAttributes & FieldAttributes.Static) != 0)
                    flags |= FieldFlags.Static;

                if ((fieldAttributes & FieldAttributes.InitOnly) != 0)
                    flags |= FieldFlags.InitOnly;

                if ((fieldAttributes & FieldAttributes.Literal) != 0)
                    flags |= FieldFlags.Literal;

                flags |= FieldFlags.BasicMetadataCache;
            }

            // Fetching custom attribute based properties is more expensive, so keep that under
            // a separate cache that might not be accessed very frequently.
            if ((mask & FieldFlags.AttributeMetadataCache) != 0)
            {
                var fieldDefinition = this.MetadataReader.GetFieldDefinition(_handle);

                foreach (var customAttributeHandle in fieldDefinition.GetCustomAttributes())
                {
                    var customAttribute = this.MetadataReader.GetCustomAttribute(customAttributeHandle);
                    var constructorHandle = customAttribute.Constructor;

                    var constructor = Module.GetMethod(constructorHandle);
                    var type = constructor.OwningType;

                    switch (type.Name)
                    {
                        case "System.ThreadStaticAttribute":
                            flags |= FieldFlags.ThreadStatic;
                            break;
                    }
                }

                flags |= FieldFlags.AttributeMetadataCache;
            }

            Debug.Assert((flags & mask) != 0);

            // Atomically update flags
            var originalFlags = _fieldFlags;
            while (Interlocked.CompareExchange(ref _fieldFlags, (int)(originalFlags | flags), originalFlags) != originalFlags)
            {
                originalFlags = _fieldFlags;
            }

            _fieldFlags |= flags;

            return flags & mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetFieldFlags(int mask)
        {
            int flags = _fieldFlags & mask;
            if (flags != 0)
                return flags;
            return InitializeFieldFlags(mask);
        }

        public override bool IsStatic
        {
            get
            {
                return (GetFieldFlags(FieldFlags.BasicMetadataCache | FieldFlags.Static) & FieldFlags.Static) != 0;
            }
        }

        public override bool IsThreadStatic
        {
            get
            {
                return IsStatic &&
                    (GetFieldFlags(FieldFlags.AttributeMetadataCache | FieldFlags.ThreadStatic) & FieldFlags.ThreadStatic) != 0;
            }
        }

        public override bool IsInitOnly
        {
            get
            {
                return (GetFieldFlags(FieldFlags.BasicMetadataCache | FieldFlags.InitOnly) & FieldFlags.InitOnly) != 0;
            }
        }

        public bool IsLiteral
        {
            get
            {
                return (GetFieldFlags(FieldFlags.BasicMetadataCache | FieldFlags.Literal) & FieldFlags.Literal) != 0;
            }
        }

        public FieldAttributes Attributes
        {
            get
            {
                var fieldDefinition = this.MetadataReader.GetFieldDefinition(_handle);
                return fieldDefinition.Attributes;
            }
        }

        public override string Name
        {
            get
            {
                var metadataReader = this.MetadataReader;
                var fieldDefinition = metadataReader.GetFieldDefinition(_handle);
                return metadataReader.GetString(fieldDefinition.Name);
            }
        }

        public override bool HasRva
        {
            get
            {
                return (FieldDefinition.Attributes & FieldAttributes.HasFieldRVA) != 0;
            }
        }

        public override string ToString()
        {
            return _type.ToString() + "." + this.Name;
        }
    }
}