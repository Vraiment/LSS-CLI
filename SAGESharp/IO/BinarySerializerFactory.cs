/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using Konvenience;
using SAGESharp.SLB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SAGESharp.IO
{
    /// <summary>
    /// Interface to create instances of <see cref="IBinarySerializer"/>.
    /// </summary>
    public interface IBinarySerializerFactory
    {
        /// <summary>
        /// Creates the corresponding <see cref="IBinarySerializer"/> instance for the given <typeparamref name="T"/>.
        /// </summary>
        /// 
        /// <typeparam name="T">The type of the object to serialize.</typeparam>
        /// 
        /// <returns>An instance of <see cref="IBinarySerializer" /> for <typeparamref name="T"/>.</returns>
        IBinarySerializer<T> GetSerializerForType<T>();
    }

    /// <summary>
    /// Static class to provide a singleton of the <see cref="IBinarySerializerFactory"/>.
    /// </summary>
    public static class BinarySerializers
    {
        private static readonly Lazy<IBinarySerializerFactory> instance
            = new Lazy<IBinarySerializerFactory>(
                () => new DefaultBinarySerializerFactory(new DefaultPropertyBinarySerializerFactory())
            );

        /// <summary>
        /// The singleton instance of the <see cref="IBinarySerializerFactory"/> interface.
        /// </summary>
        public static IBinarySerializerFactory Factory { get => instance.Value; }

        private static readonly Lazy<IBinarySerializer<BKD>> bkdBinarySerializer
            = new Lazy<IBinarySerializer<BKD>>(() => new BinarySerializableSerializer<BKD>());

        /// <summary>
        /// The singleton instance to serialize <see cref="BKD"/> files.
        /// </summary>
        public static IBinarySerializer<BKD> ForBKDFiles { get => bkdBinarySerializer.Value; }
    }

    /// <summary>
    /// Interfaces to generate a list of <see cref="IPropertyBinarySerializer{T}"/> for a given type.
    /// </summary>
    internal interface IPropertyBinarySerializerFactory
    {
        /// <summary>
        /// Inspects the type <typeparamref name="T"/> and gets the list serializers for all its serialzable properties.
        /// </summary>
        /// 
        /// <typeparam name="T">The type to inspect.</typeparam>
        /// 
        /// <param name="binarySerializerFactory">
        /// The <see cref="IBinarySerializerFactory"/> that will be used to get the serializers for the types of each property.
        /// </param>
        /// 
        /// <returns>A list of <see cref="IPropertyBinarySerializer{T}"/> objects for the type <typeparamref name="T"/>.</returns>
        /// 
        /// <exception cref="BadTypeException">
        /// If the type doesn't have any serializable property
        /// or if a property is marked as serializable but doesn't have a setter
        /// or if any of the properties has a duplicated binary order.
        /// </exception>
        IReadOnlyList<IPropertyBinarySerializer<T>> GetPropertySerializersForType<T>(IBinarySerializerFactory binarySerializerFactory);
    }

    internal sealed class DefaultBinarySerializerFactory : IBinarySerializerFactory
    {
        private readonly IPropertyBinarySerializerFactory propertyBinarySerializerFactory;

        public DefaultBinarySerializerFactory(IPropertyBinarySerializerFactory propertyBinarySerializerFactory)
            => this.propertyBinarySerializerFactory = propertyBinarySerializerFactory;

        public IBinarySerializer<T> GetSerializerForType<T>()
        {
            if (typeof(T).IsPrimitive)
            {
                return new PrimitiveBinarySerializer<T>();
            }
            else if (typeof(T) == typeof(Identifier))
            {
                dynamic result = new CastSerializer<Identifier, uint>(new PrimitiveBinarySerializer<uint>());
                return (IBinarySerializer<T>)result;
            }
            else if (typeof(T).IsEnum)
            {
                var underlyingType = Enum.GetUnderlyingType(typeof(T));

                var innerSerializer = typeof(PrimitiveBinarySerializer<>)
                    .MakeGenericType(underlyingType)
                    .GetConstructor(Array.Empty<Type>())
                    .Invoke(Array.Empty<object>());

                var result = typeof(CastSerializer<,>)
                    .MakeGenericType(typeof(T), underlyingType)
                    .GetConstructor(new Type[] { typeof(IBinarySerializer<>).MakeGenericType(underlyingType) })
                    .Invoke(new object[] { innerSerializer });

                return (IBinarySerializer<T>)result;
            }
            else if (typeof(T) == typeof(string))
            {
                dynamic result = new StringBinarySerializer();
                return (IBinarySerializer<T>)result;
            }
            else if (IsListType<T>())
            {
                var elementsType = typeof(T).GetGenericArguments()[0];

                var constructor = typeof(ListBinarySerializer<>)
                    .MakeGenericType(elementsType)
                    .GetConstructor(new Type[] { typeof(IBinarySerializer<>).MakeGenericType(elementsType) });

                var serializer = GetType()
                    .GetMethod(nameof(GetSerializerForType))
                    .MakeGenericMethod(elementsType)
                    .Invoke(this, Array.Empty<object>());

                return (IBinarySerializer<T>)constructor.Invoke(new object[] { serializer });
            }
            else if (IsConcreteClassType<T>())
            {
                return propertyBinarySerializerFactory
                    .GetPropertySerializersForType<T>(this)
                    .Let(ss => new DefaultBinarySerializer<T>(ss));
            }
            else
            {
                throw new BadTypeException(typeof(T), "Type is not a supported serializable type");
            }
        }

        private static bool IsListType<T>() => typeof(T)
            .TakeReferenceIf(t => t.IsGenericType)
            ?.GetGenericTypeDefinition()
            ?.Let(t => t == typeof(IList<>))
            ?? false;

        private static bool IsConcreteClassType<T>() => typeof(T)
            .TakeReferenceUnless(t => t.IsValueType)
            ?.TakeReferenceUnless(t => t.IsAbstract)
            ?.TakeReferenceUnless(t => t.IsInterface)
            ?.Let(t => true)
            ?? false;
    }

    internal sealed class DefaultPropertyBinarySerializerFactory : IPropertyBinarySerializerFactory
    {
        public IReadOnlyList<IPropertyBinarySerializer<T>> GetPropertySerializersForType<T>(IBinarySerializerFactory binarySerializerFactory)
            => typeof(T)
                .GetProperties()
                .Where(HasSerializableAttribute)
                .OrderBy(GetBinaryOrder)
                .Also(ValidateProperties<T>)
                .Select(p => BuildSerializer<T>(p, binarySerializerFactory))
                .ToList();

        private static bool HasSerializableAttribute(PropertyInfo propertyInfo)
            => propertyInfo.GetCustomAttribute<SerializablePropertyAttribute>() != null;

        private static int GetBinaryOrder(PropertyInfo propertyInfo)
            => propertyInfo.GetCustomAttribute<SerializablePropertyAttribute>().BinaryOrder;

        private static void ValidateProperties<T>(IEnumerable<PropertyInfo> properties)
        {
            var type = typeof(T);
            var propertiesCount = properties.Count();

            if (propertiesCount == 0)
            {
                throw BadTypeException.For<T>($"Type has no property annotated with {nameof(SerializablePropertyAttribute)}");
            }

            var orderCount = properties
                .Select(GetBinaryOrder)
                .Distinct()
                .Count();

            if (propertiesCount != orderCount)
            {
                throw BadTypeException.For<T>("Type has more than one property with the same order");
            }

            foreach (var property in properties)
            {
                if (property.GetSetMethod() is null)
                {
                    throw BadTypeException.For<T>($"Property {property.Name} doesn't have a setter");
                }
            }
        }

        private static IPropertyBinarySerializer<T> BuildSerializer<T>(PropertyInfo propertyInfo, IBinarySerializerFactory binarySerializerFactory)
            => typeof(DefaultPropertyBinarySerializer<,>)
                .MakeGenericType(new Type[] { typeof(T), propertyInfo.PropertyType })
                .GetConstructor(new Type[] { typeof(IBinarySerializer<>).MakeGenericType(propertyInfo.PropertyType), typeof(PropertyInfo) })
                .Invoke(new object[] { GetSerializerForPropertyType(propertyInfo, binarySerializerFactory), propertyInfo })
                .As<IPropertyBinarySerializer<T>>();

        private static object GetSerializerForPropertyType(PropertyInfo propertyInfo, IBinarySerializerFactory binarySerializerFactory)
            => typeof(IBinarySerializerFactory)
                .GetMethod(nameof(IBinarySerializerFactory.GetSerializerForType))
                .MakeGenericMethod(new Type[] { propertyInfo.PropertyType })
                .Invoke(binarySerializerFactory, Array.Empty<object>());
    }
}
