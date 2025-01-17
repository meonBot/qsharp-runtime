﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Quantum.Qir.Serialization;

namespace Microsoft.Quantum.Qir.Runtime.Tools.Driver
{
    public class QirCppDriverGenerator : IQirDriverGenerator
    {
        private readonly IQirRuntimeInitializer RuntimeInitalizer;

        private delegate string StringConversion(ArgumentValue value);

        public QirCppDriverGenerator(IQirRuntimeInitializer runtimeInitializer)
        {
            RuntimeInitalizer = runtimeInitializer;
        }

        public async Task GenerateAsync(EntryPointOperation entryPointOperation, Stream stream)
        {
            var qirCppDriver = new QirCppDriver(entryPointOperation, RuntimeInitalizer);
            var cppSource = qirCppDriver.TransformText();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(cppSource));
            await stream.FlushAsync();
            stream.Position = 0;
        }

        public string GetCommandLineArguments(ExecutionInformation executionInformation)
        {
            string ToArgument(Parameter param)
            {
                var prefix = param.Name.Length > 1 ? "--" : "-";
                var value = GetArgumentValueString(param, executionInformation.ArgumentValues[param.Name]);
                return $"{prefix}{param.Name} {value}";
            }

            var parameters = executionInformation.EntryPoint.Parameters.OrderBy(param => param.Position);
            return string.Join(" ", parameters.Select(ToArgument));
        }

        private static string GetArgumentValueString(Parameter argument, ArgumentValue argumentValue)
        {
            // Today, only the first argument value in the array will be used.
            return argument.Type switch
            {
                DataType.BoolType => GetBoolValueString(argumentValue),
                DataType.DoubleType => GetDoubleValueString(argumentValue),
                DataType.IntegerType => GetIntegerValueString(argumentValue),
                DataType.PauliType => GetPauliValueString(argumentValue),
                DataType.RangeType => GetRangeValueString(argumentValue),
                DataType.ResultType => GetResultValueString(argumentValue),
                DataType.StringType => GetStringValueString(argumentValue),
                DataType.ArrayType => GetArrayValueString(argument.ArrayType, argumentValue.Array),
                _ => throw new ArgumentException($"Unsupported data type {argument.Type}")
            };
        }

        private static string GetArrayValueString(DataType? arrayType, IList<ArgumentValue> arrayValue)
        {
            static string ConvertArray(IList<ArgumentValue> list, StringConversion conversion) =>
                string.Join(' ', list.Select(item => conversion.Invoke(item)));

            return arrayType switch
            {
                DataType.BoolType => ConvertArray(arrayValue, GetBoolValueString),
                DataType.DoubleType => ConvertArray(arrayValue, GetDoubleValueString),
                DataType.IntegerType => ConvertArray(arrayValue, GetIntegerValueString),
                DataType.PauliType => ConvertArray(arrayValue, GetPauliValueString),
                DataType.RangeType => ConvertArray(arrayValue, GetRangeValueString),
                DataType.ResultType => ConvertArray(arrayValue, GetResultValueString),
                DataType.StringType => ConvertArray(arrayValue, GetStringValueString),
                _ => throw new ArgumentException($"Unsupported array data type {arrayType}")
            };
        }

        private static string GetResultValueString(ArgumentValue value)
        {
            if (value?.Result == null)
            {
                throw new ArgumentNullException("Cannot convert null result value to string.");
            }
            return value.Result == ResultValue.One ? "1" : "0";
        }

        private static string GetRangeValueString(ArgumentValue value)
        {
            if (value?.Range == null)
            {
                throw new ArgumentNullException("Cannot convert null range value to string.");
            }
            var rangeValue = value.Range;
            return $"{rangeValue.Start} {rangeValue.Step} {rangeValue.End}";
        }

        private static string GetStringValueString(ArgumentValue value)
        {
            if (value?.String == null)
            {
                throw new ArgumentNullException("Cannot convert null string value to string.");
            }

            return $"\"{value.String}\"";
        }

        private static string GetBoolValueString(ArgumentValue value)
        {
            if (value?.Bool == null)
            {
                throw new ArgumentNullException("Cannot convert null bool value to string.");
            }

            return value.Bool.ToString().ToLower();
        }

        private static string GetPauliValueString(ArgumentValue value)
        {
            if (value?.Pauli == null)
            {
                throw new ArgumentNullException("Cannot convert null pauli value to string.");
            }

            return value.Pauli.ToString().ToLower();
        }

        private static string GetDoubleValueString(ArgumentValue value)
        {
            if (value?.Double == null)
            {
                throw new ArgumentNullException("Cannot convert null double value to string.");
            }

            return value.Double.ToString().ToLower();
        }

        private static string GetIntegerValueString(ArgumentValue value)
        {
            if (value?.Integer == null)
            {
                throw new ArgumentNullException("Cannot convert null integer value to string.");
            }

            return value.Integer.ToString().ToLower();
        }
    }
}
