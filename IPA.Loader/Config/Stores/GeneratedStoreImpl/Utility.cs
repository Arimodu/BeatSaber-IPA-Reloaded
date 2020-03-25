﻿using IPA.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace IPA.Config.Stores
{
    internal static partial class GeneratedStoreImpl
    {
        #region Logs
        private static readonly MethodInfo LogErrorMethod = typeof(GeneratedStoreImpl).GetMethod(nameof(LogError), BindingFlags.NonPublic | BindingFlags.Static);
        internal static void LogError(Type expected, Type found, string message)
        {
            Logger.config.Notice($"{message}{(expected == null ? "" : $" (expected {expected}, found {found?.ToString() ?? "null"})")}");
        }
        private static readonly MethodInfo LogWarningMethod = typeof(GeneratedStoreImpl).GetMethod(nameof(LogWarning), BindingFlags.NonPublic | BindingFlags.Static);
        internal static void LogWarning(string message)
        {
            Logger.config.Warn(message);
        }
        private static readonly MethodInfo LogWarningExceptionMethod = typeof(GeneratedStoreImpl).GetMethod(nameof(LogWarningException), BindingFlags.NonPublic | BindingFlags.Static);
        internal static void LogWarningException(Exception exception)
        {
            Logger.config.Warn(exception);
        }
        #endregion

        private delegate LocalBuilder GetLocal(Type type, int idx = 0);

        private static GetLocal MakeGetLocal(ILGenerator il)
        { // TODO: improve this shit a bit so that i can release a hold of a variable and do more auto managing
            var locals = new List<LocalBuilder>();

            LocalBuilder GetLocal(Type ty, int i = 0)
            {
                var builder = locals.Where(b => b.LocalType == ty).Skip(i).FirstOrDefault();
                if (builder == null)
                {
                    builder = il.DeclareLocal(ty);
                    locals.Add(builder);
                }
                return builder;
            }

            return GetLocal;
        }

        private static void EmitLoad(ILGenerator il, SerializedMemberInfo member, Action<ILGenerator> thisarg)
        {
            thisarg ??= il => il.Emit(OpCodes.Ldarg_0);

            thisarg(il); // load this

            if (member.IsField)
                il.Emit(OpCodes.Ldfld, member.Member as FieldInfo);
            else
            { // member is a property
                var prop = member.Member as PropertyInfo;
                var getter = prop.GetGetMethod();
                if (getter == null) throw new InvalidOperationException($"Property {member.Name} does not have a getter and is not ignored");

                il.Emit(OpCodes.Call, getter);
            }
        }

        private static void EmitStore(ILGenerator il, SerializedMemberInfo member, Action<ILGenerator> value, Action<ILGenerator> thisobj)
        {
            thisobj(il);
            value(il);

            if (member.IsField)
                il.Emit(OpCodes.Stfld, member.Member as FieldInfo);
            else
            { // member is a property
                var prop = member.Member as PropertyInfo;
                var setter = prop.GetSetMethod();
                if (setter == null) throw new InvalidOperationException($"Property {member.Name} does not have a setter and is not ignored");

                il.Emit(OpCodes.Call, setter);
            }
        }

        private static void EmitWarnException(ILGenerator il, string v)
        {
            il.Emit(OpCodes.Ldstr, v);
            il.Emit(OpCodes.Call, LogWarningMethod);
            il.Emit(OpCodes.Call, LogWarningExceptionMethod);
        }

        private static void EmitLogError(ILGenerator il, string message, bool tailcall = false, Action<ILGenerator> expected = null, Action<ILGenerator> found = null)
        {
            if (expected == null) expected = il => il.Emit(OpCodes.Ldnull);
            if (found == null) found = il => il.Emit(OpCodes.Ldnull);

            expected(il);
            found(il);
            il.Emit(OpCodes.Ldstr, message);
            if (tailcall) il.Emit(OpCodes.Tailcall);
            il.Emit(OpCodes.Call, LogErrorMethod);
        }

        private static readonly MethodInfo Type_GetTypeFromHandle = typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle));
        private static void EmitTypeof(ILGenerator il, Type type)
        {
            il.Emit(OpCodes.Ldtoken, type);
            il.Emit(OpCodes.Call, Type_GetTypeFromHandle);
        }

        private static readonly Type IDisposable_t = typeof(IDisposable);
        private static readonly MethodInfo IDisposable_Dispose = IDisposable_t.GetMethod(nameof(IDisposable.Dispose));

        private static readonly Type Decimal_t = typeof(decimal);
        private static readonly ConstructorInfo Decimal_FromFloat = Decimal_t.GetConstructor(new[] { typeof(float) });
        private static readonly ConstructorInfo Decimal_FromDouble = Decimal_t.GetConstructor(new[] { typeof(double) });
        private static readonly ConstructorInfo Decimal_FromInt = Decimal_t.GetConstructor(new[] { typeof(int) });
        private static readonly ConstructorInfo Decimal_FromUInt = Decimal_t.GetConstructor(new[] { typeof(uint) });
        private static readonly ConstructorInfo Decimal_FromLong = Decimal_t.GetConstructor(new[] { typeof(long) });
        private static readonly ConstructorInfo Decimal_FromULong = Decimal_t.GetConstructor(new[] { typeof(ulong) });
        private static void EmitNumberConvertTo(ILGenerator il, Type to, Type from)
        { // WARNING: THIS USES THE NO-OVERFLOW OPCODES
            if (to == from) return;
            if (to == Decimal_t)
            {
                if (from == typeof(float)) il.Emit(OpCodes.Newobj, Decimal_FromFloat);
                else if (from == typeof(double)) il.Emit(OpCodes.Newobj, Decimal_FromDouble);
                else if (from == typeof(long)) il.Emit(OpCodes.Newobj, Decimal_FromLong);
                else if (from == typeof(ulong)) il.Emit(OpCodes.Newobj, Decimal_FromULong);
                else if (from == typeof(int)) il.Emit(OpCodes.Newobj, Decimal_FromInt);
                else if (from == typeof(uint)) il.Emit(OpCodes.Newobj, Decimal_FromUInt);
                else if (from == typeof(IntPtr))
                {
                    EmitNumberConvertTo(il, typeof(long), from);
                    EmitNumberConvertTo(il, to, typeof(long));
                }
                else if (from == typeof(UIntPtr))
                {
                    EmitNumberConvertTo(il, typeof(ulong), from);
                    EmitNumberConvertTo(il, to, typeof(ulong));
                }
                else
                { // if the source is anything else, we first convert to int because that can contain all other values
                    EmitNumberConvertTo(il, typeof(int), from);
                    EmitNumberConvertTo(il, to, typeof(int));
                };
            }
            else if (from == Decimal_t)
            {
                if (to == typeof(IntPtr))
                {
                    EmitNumberConvertTo(il, typeof(long), from);
                    EmitNumberConvertTo(il, to, typeof(long));
                }
                else if (to == typeof(UIntPtr))
                {
                    EmitNumberConvertTo(il, typeof(ulong), from);
                    EmitNumberConvertTo(il, to, typeof(ulong));
                }
                else
                {
                    var method = Decimal_t.GetMethod($"To{to.Name}"); // conveniently, this is the pattern of the to* names
                    il.Emit(OpCodes.Call, method);
                }
            }
            else if (to == typeof(IntPtr)) il.Emit(OpCodes.Conv_I);
            else if (to == typeof(UIntPtr)) il.Emit(OpCodes.Conv_U);
            else if (to == typeof(sbyte)) il.Emit(OpCodes.Conv_I1);
            else if (to == typeof(byte)) il.Emit(OpCodes.Conv_U1);
            else if (to == typeof(short)) il.Emit(OpCodes.Conv_I2);
            else if (to == typeof(ushort)) il.Emit(OpCodes.Conv_U2);
            else if (to == typeof(int)) il.Emit(OpCodes.Conv_I4);
            else if (to == typeof(uint)) il.Emit(OpCodes.Conv_U4);
            else if (to == typeof(long)) il.Emit(OpCodes.Conv_I8);
            else if (to == typeof(ulong)) il.Emit(OpCodes.Conv_U8);
            else if (to == typeof(float))
            {
                if (from == typeof(byte)
                 || from == typeof(ushort)
                 || from == typeof(uint)
                 || from == typeof(ulong)
                 || from == typeof(UIntPtr)) il.Emit(OpCodes.Conv_R_Un);
                il.Emit(OpCodes.Conv_R4);
            }
            else if (to == typeof(double))
            {
                if (from == typeof(byte)
                 || from == typeof(ushort)
                 || from == typeof(uint)
                 || from == typeof(ulong)
                 || from == typeof(UIntPtr)) il.Emit(OpCodes.Conv_R_Un);
                il.Emit(OpCodes.Conv_R8);
            }
        }

        private static void EmitCreateChildGenerated(ILGenerator il, Type childType, Action<ILGenerator> parentobj)
        {
            var method = CreateGParent.MakeGenericMethod(childType);
            parentobj(il);
            il.Emit(OpCodes.Call, method);
        }
    }
}
