﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Wheatech.Properties;

namespace Wheatech
{
    /// <summary>
    /// Provides a set of methods to convert value.
    /// </summary>
    public static class ObjectUtils
    {
        private class TypeDescriptorContext : ITypeDescriptorContext
        {
            public TypeDescriptorContext(PropertyDescriptor propDesc, object instance)
            {
                PropertyDescriptor = propDesc;
                Instance = instance;
            }

            public object GetService(Type serviceType)
            {
                return null;
            }

            public bool OnComponentChanging()
            {
                return true;
            }

            public void OnComponentChanged()
            {
            }

            public IContainer Container => null;

            public object Instance { get; }

            public PropertyDescriptor PropertyDescriptor { get; }
        }

        private static readonly ConcurrentDictionary<Tuple<Type, Type>, Func<ITypeDescriptorContext, object, object>> _converters = new ConcurrentDictionary<Tuple<Type, Type>, Func<ITypeDescriptorContext, object, object>>();

        /// <summary>
        /// Create new instance of <see cref="ITypeDescriptorContext"/>.
        /// </summary>
        /// <param name="instance">The relative object instance.</param>
        /// <param name="property">The relative property.</param>
        /// <returns>An instance of <see cref="ITypeDescriptorContext"/>.</returns>
        public static ITypeDescriptorContext CreateTypeDescriptorContext(object instance, PropertyDescriptor property)
        {
            return new TypeDescriptorContext(property, instance);
        }

        /// <summary>
        /// Creates a data object and sets the properties specified by name/value pairs in the dictionary.
        /// </summary>
        /// <param name="component">The data object to set properties.</param>
        /// <param name="fieldValues">The dictionary contains the name/value pairs.</param>
        /// <exception cref="AggregateException">Any property cannot be converted from <paramref name="fieldValues"/>.</exception>
        public static void BuildObject(object component, IDictionary fieldValues)
        {
            var properties = TypeDescriptor.GetProperties(component);
            var validationErrors = new List<Exception>();
            foreach (DictionaryEntry entry in fieldValues)
            {
                string name = entry.Key.ToString();
                var descriptor = properties.Find(name, true);
                if (descriptor != null && !descriptor.IsReadOnly)
                {
                    object value = entry.Value;
                    try
                    {
                        value = ConvertValue(value, descriptor.PropertyType, descriptor.Converter, CreateTypeDescriptorContext(component, descriptor));
                    }
                    catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is OverflowException)
                    {
                        validationErrors.Add(new FormatException(string.Format(CultureInfo.CurrentCulture, Strings.Cannot_Convert_Field, value, descriptor.PropertyType, name), ex));
                        continue;
                    }
                    var underlyingType = Nullable.GetUnderlyingType(descriptor.PropertyType);
                    if (value != null && underlyingType != null)
                    {
                        var type = value.GetType();
                        if (underlyingType != type)
                        {
                            validationErrors.Add(new FormatException(string.Format(CultureInfo.CurrentCulture, Strings.Cannot_Convert_Field, value, $"Nullable<{underlyingType.FullName}>", name)));
                            continue;
                        }
                    }
                    descriptor.SetValue(component, value);
                }
            }
            if (validationErrors.Count > 0)
            {
                throw new AggregateException(validationErrors);
            }
        }

        /// <summary>
        /// Converts the given object to the given type.
        /// </summary>
        /// <param name="value">An object that to be converted.</param>
        /// <param name="destinationType">The type of object to be converted to.</param>
        /// <returns>An object whose type is <paramref name="destinationType"/> and whose value is equivalent to <paramref name="value"/>.</returns>
        /// <exception cref="InvalidOperationException">This conversion is not supported.</exception>
        public static object ConvertValue(object value, Type destinationType)
        {
            return ConvertValue(value, destinationType, null, null);
        }

        /// <summary>
        /// Converts the given object to the given type by using the specified converter and context.
        /// </summary>
        /// <param name="value">An object that to be converted.</param>
        /// <param name="destinationType">The type of object to be converted to.</param>
        /// <param name="converter">The converter to convert value.</param>
        /// <param name="context">The type descriptor context.</param>
        /// <returns>An object whose type is <paramref name="destinationType"/> and whose value is equivalent to <paramref name="value"/>.</returns>
        /// <exception cref="InvalidOperationException">This conversion is not supported.</exception>
        public static object ConvertValue(object value, Type destinationType, TypeConverter converter, ITypeDescriptorContext context = null)
        {
            return ConvertValue(value, destinationType, converter, context, null);
        }

        /// <summary>
        /// Converts the given object to the given type.
        /// </summary>
        /// <param name="value">An object that to be converted.</param>
        /// <param name="destinationType">The type of object to be converted to.</param>
        /// <param name="name">The target member name.</param>
        /// <returns>An object whose type is <paramref name="destinationType"/> and whose value is equivalent to <paramref name="value"/>.</returns>
        /// <exception cref="InvalidOperationException">This conversion is not supported.</exception>
        public static object ConvertValue(object value, Type destinationType, string name)
        {
            return ConvertValue(value, destinationType, null, null, name);
        }

        private static object ConvertValue(object value, Type destinationType, TypeConverter converter, ITypeDescriptorContext context, string name)
        {
            if (value == null || destinationType.IsInstanceOfType(value)) return value;
            if (converter != null && converter.CanConvertFrom(value.GetType()))
            {
                return converter.ConvertFrom(value);
            }
            if (destinationType.IsArray && value.GetType().IsArray)
            {
                var sourceArray = (Array)value;
                var lengths = new int[sourceArray.Rank];
                for (int i = 0; i < sourceArray.Rank; i++)
                {
                    lengths[i] = sourceArray.GetLength(i);
                }
                var targetElementType = destinationType.GetElementType();
                var targetArray = Array.CreateInstance(targetElementType, lengths);
                for (int i = 0; i < sourceArray.Length; i++)
                {
                    targetArray.SetValue(ConvertValue(sourceArray.GetValue(i), targetElementType, converter, context, name), i);
                }
                return targetArray;
            }
            Type elementType = destinationType;
            bool isNullable = false;
            if (destinationType.IsGenericType && destinationType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                elementType = destinationType.GetGenericArguments()[0];
                isNullable = true;
            }
            else if (destinationType.IsByRef)
            {
                elementType = destinationType.GetElementType();
            }

            // Convert empty string to null
            if (isNullable && value is string && string.IsNullOrEmpty((string)value))
            {
                return null;
            }
            return ConvertParameterType(value, elementType, context, name);
        }

        private static Func<ITypeDescriptorContext, object, object> CreateConverter(Type sourceType, Type destinationType)
        {
            var convertMethods = destinationType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m => m.GetParameters().Length == 1 && m.ReturnType == destinationType).ToArray();
            var method =
                convertMethods.FirstOrDefault(
                    m => m.Name == "op_Implicit" && m.GetParameters()[0].ParameterType == sourceType) ??
                convertMethods.FirstOrDefault(
                    m => m.Name == "op_Implicit" && m.GetParameters()[0].ParameterType.IsAssignableFrom(sourceType));
            if (method != null)
            {
                var parameter = Expression.Parameter(typeof(object));
                var parameterType = method.GetParameters()[0].ParameterType;
                var converter = Expression.Lambda<Func<object, object>>(Expression.TypeAs(Expression.Call(method, parameterType.IsValueType
                    ? Expression.Convert(parameter, parameterType)
                    : Expression.TypeAs(parameter, parameterType)), typeof(object)), parameter).Compile();
                return (context, value) => converter(value);
            }

            convertMethods = sourceType.GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m =>
            {
                var parameters = m.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == sourceType;
            }).ToArray();
            method =
                convertMethods.FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == destinationType) ??
                convertMethods.FirstOrDefault(m => m.Name == "op_Implicit" && destinationType.IsAssignableFrom(m.ReturnType)) ??
                convertMethods.FirstOrDefault(m => m.Name == "op_Explicit" && m.ReturnType == destinationType) ??
                convertMethods.FirstOrDefault(m => m.Name == "op_Explicit" && destinationType.IsAssignableFrom(m.ReturnType));
            if (method != null)
            {
                var parameter = Expression.Parameter(typeof(object));
                var converter = Expression.Lambda<Func<object, object>>(Expression.TypeAs(Expression.Call(method, sourceType.IsValueType
                    ? Expression.Convert(parameter, sourceType)
                    : Expression.TypeAs(parameter, sourceType)), typeof(object)), parameter).Compile();
                return (context, value) => converter(value);
            }
            var sourceConverter = TypeDescriptor.GetConverter(sourceType);
            if (sourceConverter.CanConvertTo(destinationType))
            {
                return (context, value) => sourceConverter.ConvertTo(context, CultureInfo.CurrentCulture, value, destinationType);
            }
            var targetConverter = TypeDescriptor.GetConverter(destinationType);
            if (targetConverter.CanConvertFrom(sourceType))
            {
                return (context, value) => targetConverter.ConvertFrom(context, CultureInfo.CurrentCulture, value);
            }
            return null;
        }

        private static object ConvertParameterType(object value, Type type, ITypeDescriptorContext context, string name)
        {
            if (type.IsInstanceOfType(value)) return value;
            var converter = _converters.GetOrAdd(Tuple.Create(value.GetType(), type), key => CreateConverter(key.Item1, key.Item2));
            if (converter != null)
            {
                return converter(context, value);
            }
            if (string.IsNullOrEmpty(name) && context?.PropertyDescriptor != null)
            {
                name = context.PropertyDescriptor.Name;
            }
            if (!string.IsNullOrEmpty(name))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Cannot_Convert_Value, name, typeof(string).FullName, type.FullName));
            }
            else
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Cannot_Convert_Type, value.GetType(), type));
            }
        }

        /// <summary>
        /// Returns whether the given object can be converted to the specified type.
        /// </summary>
        /// <param name="value">An object that to be converted.</param>
        /// <param name="destinationType">The type of object to be converted to.</param>
        /// <returns><c>true</c> if given object can perform the conversion; otherwise, <c>false</c>.</returns>
        public static bool CanConvertValue(object value, Type destinationType)
        {
            return CanConvertValue(value, destinationType, null);
        }

        /// <summary>
        /// Returns whether the given object can be converted to the specified type by using the specified converter.
        /// </summary>
        /// <param name="value">An object that to be converted.</param>
        /// <param name="destinationType">The type of object to be converted to.</param>
        /// <param name="converter">The converter to convert value.</param>
        /// <returns><c>true</c> if given object can perform the conversion; otherwise, <c>false</c>.</returns>
        public static bool CanConvertValue(object value, Type destinationType, TypeConverter converter)
        {
            if (value == null) return !destinationType.IsValueType;
            if (destinationType.IsInstanceOfType(value)) return true;
            var sourceType = value.GetType();
            if (converter != null && converter.CanConvertFrom(sourceType)) return true;
            if (destinationType.IsArray && value.GetType().IsArray)
            {
                return CanConvertType(sourceType.GetElementType(), destinationType.GetElementType());
            }
            Type elementType = destinationType;
            bool isNullable = false;
            if (destinationType.IsGenericType && destinationType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                elementType = destinationType.GetGenericArguments()[0];
                isNullable = true;
            }
            else if (destinationType.IsByRef)
            {
                elementType = destinationType.GetElementType();
            }

            // Convert empty string to null
            if (isNullable && value is string && string.IsNullOrEmpty((string)value))
            {
                return true;
            }
            return CanConvertType(sourceType, elementType);
        }

        /// <summary>
        /// Returns whether the given type can be converted to the specified type by using the specified converter.
        /// </summary>
        /// <param name="sourceType">The given type that to be converted.</param>
        /// <param name="destinationType">The type of object to be converted to.</param>
        /// <returns><c>true</c> if given type can perform the conversion; otherwise, <c>false</c>.</returns>
        public static bool CanConvertType(Type sourceType, Type destinationType)
        {
            if (destinationType.IsAssignableFrom(sourceType)) return true;
            var convertMethods = destinationType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m => m.GetParameters().Length == 1 && m.ReturnType == destinationType).ToArray();
            var method = convertMethods.FirstOrDefault(
                m => m.Name == "op_Implicit" && m.GetParameters()[0].ParameterType == sourceType) ??
            convertMethods.FirstOrDefault(
                m => m.Name == "op_Implicit" && m.GetParameters()[0].ParameterType.IsAssignableFrom(sourceType));
            if (method != null) return true;
            method = convertMethods.FirstOrDefault(
                m => m.Name == "op_Explicit" && m.GetParameters()[0].ParameterType == sourceType) ??
            convertMethods.FirstOrDefault(
                m => m.Name == "op_Explicit" && m.GetParameters()[0].ParameterType.IsAssignableFrom(sourceType));
            if (method != null) return true;
            convertMethods = sourceType.GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m =>
            {
                var parameters = m.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == sourceType;
            }).ToArray();
            method =
                convertMethods.FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == destinationType) ??
                convertMethods.FirstOrDefault(m => m.Name == "op_Implicit" && destinationType.IsAssignableFrom(m.ReturnType));
            if (method != null) return true;
            method =
                convertMethods.FirstOrDefault(m => m.Name == "op_Explicit" && m.ReturnType == destinationType) ??
                convertMethods.FirstOrDefault(m => m.Name == "op_Explicit" && destinationType.IsAssignableFrom(m.ReturnType));
            if (method != null) return true;
            var sourceConverter = TypeDescriptor.GetConverter(sourceType);
            if (sourceConverter.CanConvertTo(destinationType)) return true;
            TypeConverter targetConverter = TypeDescriptor.GetConverter(destinationType);
            if (targetConverter.CanConvertFrom(sourceType)) return true;
            return false;
        }
    }
}
