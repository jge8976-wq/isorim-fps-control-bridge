using System;
using System.Linq.Expressions;
using System.Reflection;

namespace HSK.FpsBridge
{
    // Reads of private fields compiled once (expression trees) for the paths run per figure or per drawn part every
    // frame, where FieldInfo.GetValue's reflection cost adds up with many pawns on the map.
    internal static class FastField
    {
        public static Func<object, T> Getter<T>(FieldInfo f)
        {
            ParameterExpression o = Expression.Parameter(typeof(object), "o");
            Expression read = Expression.Field(Expression.Convert(o, f.DeclaringType), f);
            return Expression.Lambda<Func<object, T>>(Expression.Convert(read, typeof(T)), o).Compile();
        }

        public static Func<T> Static<T>(FieldInfo f) =>
            Expression.Lambda<Func<T>>(Expression.Convert(Expression.Field(null, f), typeof(T))).Compile();
    }
}
