﻿namespace Owin.Scim.Querying
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;

    using Antlr;
    using Antlr4.Runtime.Misc;
    using Antlr4.Runtime.Tree;

    using Extensions;

    using Model;

    using NContext.Common;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Newtonsoft.Json.Serialization;

    public class ScimFilterVisitor<TResource> : ScimFilterBaseVisitor<LambdaExpression>, IScimFilterVisitor
    {
        private static readonly ConcurrentDictionary<Type, IDictionary<string, PropertyInfo>> _PropertyCache = 
            new ConcurrentDictionary<Type, IDictionary<string, PropertyInfo>>();

        private static readonly Lazy<IDictionary<string, MethodInfo>> _MethodCache =
            new Lazy<IDictionary<string, MethodInfo>>(CreateMethodCache);

        private static readonly JsonSerializer _JsonSerializer;

        static ScimFilterVisitor()
        {
            _JsonSerializer = new JsonSerializer
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                DateTimeZoneHandling = DateTimeZoneHandling.Utc
            };
        }

        protected static IDictionary<string, MethodInfo> MethodCache
        {
            get { return _MethodCache.Value; }
        }

        protected static ConcurrentDictionary<Type, IDictionary<string, PropertyInfo>> PropertyCache
        {
            get { return _PropertyCache; }
        }

        public LambdaExpression VisitExpression(IParseTree tree)
        {
            return Visit(tree);
        }

        public override LambdaExpression VisitAndExp(ScimFilterParser.AndExpContext context)
        {
            var left = Visit(context.filter(0));
            var right = Visit(context.filter(1));

            var parameter = Expression.Parameter(typeof(TResource));
            var resultBody = Expression.And(Expression.Invoke(left, parameter), Expression.Invoke(right, parameter));

            return Expression.Lambda<Func<TResource, bool>>(resultBody, parameter);
        }

        public override LambdaExpression VisitBraceExp(ScimFilterParser.BraceExpContext context)
        {
            var predicate = Visit(context.filter());
            if (context.NOT() != null)
            {
                var parameter = Expression.Parameter(typeof(TResource));
                var resultBody = Expression.Not(Expression.Invoke(predicate, parameter));

                return Expression.Lambda<Func<TResource, bool>>(resultBody, parameter);
            }

            return predicate;
        }

        public override LambdaExpression VisitValPathExp(ScimFilterParser.ValPathExpContext context)
        {
            // brackets MAY change the field type (TResource) thus, the expression within the brackets 
            // should be visited in context of the new field's type

            var propertyNameToken = context.FIELD().GetText();
            var property = PropertyCache
                .GetOrAdd(
                    typeof(TResource), 
                    type => 
                    type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .ToDictionary(pi => pi.Name, pi => pi, StringComparer.OrdinalIgnoreCase))[propertyNameToken];

            if (property == null) throw new Exception("ERROR"); // TODO: (DG) make proper error

            if (property.PropertyType != typeof (TResource))
            {
                bool isEnumerable = property.PropertyType.IsNonStringEnumerable();
                Type childFilterType = property.PropertyType;
                if (isEnumerable)
                {
                    childFilterType = childFilterType.GetGenericArguments()[0]; // set childFilterType to enumerable type argument
                }

                var argument = Expression.Parameter(typeof(TResource));
                var childVisitorType = typeof (ScimFilterVisitor<>).MakeGenericType(childFilterType);
                var childVisitor = (IScimFilterVisitor) childVisitorType.CreateInstance();
                var childLambda = childVisitor.VisitExpression(context.valPathFilter()); // Visit the nested filter expression.
                var childLambdaArgument = Expression.TryCatch(
                    Expression.Block(Expression.Property(argument, property)),
                    Expression.Catch(typeof (Exception),
                        Expression.Constant(property.PropertyType.GetDefaultValue(), property.PropertyType))
                    );

                if (isEnumerable)
                {
                    // if we have an enumerable, then we need to see if any of its elements satisfy the childLambda
                    // to accomplish this, let's just make use of .NET's Any<TSource>(enumerable, predicate)

                    var anyMethod = MethodCache["any"].MakeGenericMethod(childFilterType);
                    var anyPredicate = Expression.TryCatch(
                        Expression.Block(
                            Expression.Call(
                                anyMethod,
                                new List<Expression>
                                {
                                    childLambdaArgument,
                                    childLambda
                                })),
                        Expression.Catch(typeof (ArgumentNullException), Expression.Constant(false)));

                    return Expression.Lambda(anyPredicate, argument);
                }

                return Expression.Lambda(
                    Expression.Invoke(
                        childLambda,
                        new List<Expression>
                        {
                            Expression.TypeAs(childLambdaArgument, childFilterType)
                        }),
                    argument);
            }

            // TODO: (DG) This is probably incorrect if the property is nested and the same type as its parent.
            // We'll most likely still need a childLambda.
            return Visit(context.valPathFilter());
        }

//        public override LambdaExpression VisitNotExp(ScimFilterParser.NotExpContext context)
//        {
//            var predicate = Visit(context.expression());

//            var parameter = Expression.Parameter(typeof(TResource));
//            var resultBody = Expression.Not(Expression.Invoke(predicate, parameter));

//            return Expression.Lambda<Func<TResource, bool>>(resultBody, parameter);
//        }

        public override LambdaExpression VisitOperatorExp(ScimFilterParser.OperatorExpContext context)
        {
            var propertyNameToken = context.FIELD().GetText();
            var operatorToken = context.COMPAREOPERATOR().GetText().ToLower();
            var valueToken = context.VALUE().GetText().Trim('"');

            var property = PropertyCache
                .GetOrAdd(
                    typeof(TResource),
                    type =>
                    type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .ToDictionary(pi => pi.Name, pi => pi, StringComparer.OrdinalIgnoreCase))[propertyNameToken];

            if (property == null) throw new Exception("ERROR"); // TODO: (DG) make proper error

            var isEnumerable = property.PropertyType.IsNonStringEnumerable();
            var argument = Expression.Parameter(typeof(TResource));
            var left = Expression.TryCatch(
                Expression.Block(Expression.Property(argument, property)),
                Expression.Catch(
                    typeof(NullReferenceException),
                    Expression.Constant(property.PropertyType.GetDefaultValue(), property.PropertyType))
                );

            if (isEnumerable && 
                property.PropertyType.IsGenericType && 
                typeof(MultiValuedAttribute).IsAssignableFrom(property.PropertyType.GetGenericArguments()[0]))
            {
                // we're filtering an enumerable of multivaluedattribute without a sub-attribute
                // therefore, we default to evaluating the .Value member

                var multiValuedAttributeType = property.PropertyType.GetGenericArguments()[0];
                var multiValuedAttribute = Expression.Parameter(multiValuedAttributeType);
                var valueAttribute = multiValuedAttributeType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                var valueExpression = Expression.TryCatch(
                    Expression.Block(Expression.Property(multiValuedAttribute, valueAttribute)),
                    Expression.Catch(
                        typeof (NullReferenceException),
                        Expression.Constant(valueAttribute.PropertyType.GetDefaultValue(), valueAttribute.PropertyType))
                    );

                var valueLambda = Expression.Lambda(
                    CreateBinaryExpression(valueExpression, valueAttribute, operatorToken, valueToken),
                    multiValuedAttribute);

                var anyMethod = MethodCache["any"].MakeGenericMethod(multiValuedAttributeType);
                var anyPredicate = Expression.TryCatch(
                        Expression.Block(
                            Expression.Call(
                                anyMethod,
                                new List<Expression>
                                {
                                    left,
                                    valueLambda
                                })),
                        Expression.Catch(typeof(ArgumentNullException), Expression.Constant(false)));

                 return Expression.Lambda(anyPredicate, argument);
            }
            
            return Expression.Lambda<Func<TResource, bool>>(
                CreateBinaryExpression(left, property, operatorToken, valueToken),
                argument);
        }

        public override LambdaExpression VisitOrExp(ScimFilterParser.OrExpContext context)
        {
            var left = Visit(context.filter(0));
            var right = Visit(context.filter(1));

            var parameter = Expression.Parameter(typeof(TResource));
            var resultBody = Expression.Or(Expression.Invoke(left, parameter), Expression.Invoke(right, parameter));

            return Expression.Lambda<Func<TResource, bool>>(resultBody, parameter);
        }

        public override LambdaExpression VisitPresentExp(ScimFilterParser.PresentExpContext context)
        {
            var propertyNameToken = context.FIELD().GetText();

            var property = PropertyCache
                .GetOrAdd(
                    typeof(TResource),
                    type =>
                    type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .ToDictionary(pi => pi.Name, pi => pi, StringComparer.OrdinalIgnoreCase))[propertyNameToken];

            if (property == null) throw new Exception("eeeerrrooorrr"); // TODO: (DG) proper error handling
            if (property.GetGetMethod() == null) throw new Exception("error");
            
            var argument = Expression.Parameter(typeof(TResource));
            var predicate = Expression.Lambda<Func<TResource, bool>>(
                Expression.Call(
                    MethodCache["pr"]
                    .MakeGenericMethod(typeof(TResource)),
                        new List<Expression>
                        {
                            argument,
                            Expression.Constant(property)
                        }),
                argument);

            return predicate;
        }

        public override LambdaExpression VisitValPathAndExp([NotNull] ScimFilterParser.ValPathAndExpContext context)
        {
            var left = Visit(context.valPathFilter(0));
            var right = Visit(context.valPathFilter(1));

            var parameter = Expression.Parameter(typeof(TResource));
            var resultBody = Expression.And(Expression.Invoke(left, parameter), Expression.Invoke(right, parameter));

            return Expression.Lambda<Func<TResource, bool>>(resultBody, parameter);
        }

        public override LambdaExpression VisitValPathBraceExp(ScimFilterParser.ValPathBraceExpContext context)
        {
            var predicate = Visit(context.valPathFilter());
            if (context.NOT() != null)
            {
                var parameter = Expression.Parameter(typeof(TResource));
                var resultBody = Expression.Not(Expression.Invoke(predicate, parameter));

                return Expression.Lambda<Func<TResource, bool>>(resultBody, parameter);
            }

            return predicate;
        }

        public override LambdaExpression VisitValPathOperatorExp(ScimFilterParser.ValPathOperatorExpContext context)
        {
            var propertyNameToken = context.FIELD().GetText();
            var operatorToken = context.COMPAREOPERATOR().GetText().ToLower();
            var valueToken = context.VALUE().GetText().Trim('"');

            var property = PropertyCache
                .GetOrAdd(
                    typeof(TResource),
                    type =>
                    type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .ToDictionary(pi => pi.Name, pi => pi, StringComparer.OrdinalIgnoreCase))[propertyNameToken];

            if (property == null) throw new Exception("ERROR"); // TODO: (DG) make proper error

            var isEnumerable = property.PropertyType.IsNonStringEnumerable();
            var argument = Expression.Parameter(typeof(TResource));
            var left = Expression.TryCatch(
                Expression.Block(Expression.Property(argument, property)),
                Expression.Catch(
                    typeof(NullReferenceException),
                    Expression.Constant(property.PropertyType.GetDefaultValue(), property.PropertyType))
                );

            if (isEnumerable &&
                property.PropertyType.IsGenericType &&
                typeof(MultiValuedAttribute).IsAssignableFrom(property.PropertyType.GetGenericArguments()[0]))
            {
                // we're filtering an enumerable of multivaluedattribute without a sub-attribute
                // therefore, we default to evaluating the .Value member

                var multiValuedAttributeType = property.PropertyType.GetGenericArguments()[0];
                var multiValuedAttribute = Expression.Parameter(multiValuedAttributeType);
                var valueAttribute = multiValuedAttributeType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                var valueExpression = Expression.TryCatch(
                    Expression.Block(Expression.Property(multiValuedAttribute, valueAttribute)),
                    Expression.Catch(
                        typeof(NullReferenceException),
                        Expression.Constant(valueAttribute.PropertyType.GetDefaultValue(), valueAttribute.PropertyType))
                    );

                var valueLambda = Expression.Lambda(
                    CreateBinaryExpression(valueExpression, valueAttribute, operatorToken, valueToken),
                    multiValuedAttribute);

                var anyMethod = MethodCache["any"].MakeGenericMethod(multiValuedAttributeType);
                var anyPredicate = Expression.TryCatch(
                        Expression.Block(
                            Expression.Call(
                                anyMethod,
                                new List<Expression>
                                {
                                    left,
                                    valueLambda
                                })),
                        Expression.Catch(typeof(ArgumentNullException), Expression.Constant(false)));

                return Expression.Lambda(anyPredicate, argument);
            }

            return Expression.Lambda<Func<TResource, bool>>(
                CreateBinaryExpression(left, property, operatorToken, valueToken),
                argument);
        }

        public override LambdaExpression VisitValPathOrExp(ScimFilterParser.ValPathOrExpContext context)
        {
            var left = Visit(context.valPathFilter(0));
            var right = Visit(context.valPathFilter(1));

            var parameter = Expression.Parameter(typeof(TResource));
            var resultBody = Expression.Or(Expression.Invoke(left, parameter), Expression.Invoke(right, parameter));

            return Expression.Lambda<Func<TResource, bool>>(resultBody, parameter);
        }

        public override LambdaExpression VisitValPathPresentExp(ScimFilterParser.ValPathPresentExpContext context)
        {
            var propertyNameToken = context.FIELD().GetText();

            var property = PropertyCache
                .GetOrAdd(
                    typeof(TResource),
                    type =>
                    type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .ToDictionary(pi => pi.Name, pi => pi, StringComparer.OrdinalIgnoreCase))[propertyNameToken];

            if (property == null) throw new Exception("eeeerrrooorrr"); // TODO: (DG) proper error handling
            if (property.GetGetMethod() == null) throw new Exception("error");

            var argument = Expression.Parameter(typeof(TResource));
            var predicate = Expression.Lambda<Func<TResource, bool>>(
                Expression.Call(
                    MethodCache["pr"]
                    .MakeGenericMethod(typeof(TResource)),
                        new List<Expression>
                        {
                            argument,
                            Expression.Constant(property)
                        }),
                argument);

            return predicate;
        }

        private Expression CreateBinaryExpression(Expression left, PropertyInfo property, string operatorToken, string valueToken)
        {
            // Equal
            if (operatorToken.Equals("eq"))
            {
                int intValue;
                if (property.PropertyType == typeof(int) && int.TryParse(valueToken, out intValue))
                {
                    return Expression.Equal(left, Expression.Constant(intValue));
                }

                bool boolValue;
                if (property.PropertyType == typeof(bool) && bool.TryParse(valueToken, out boolValue))
                {
                    return Expression.Equal(left, Expression.Constant(boolValue));
                }
                
                if (property.PropertyType == typeof(DateTime))
                {
                    return Expression.Equal(left, Expression.Constant(ParseDateTime(valueToken)));
                }

                if (property.PropertyType != typeof(string))
                {
                    return Expression.Equal(left, Expression.Constant(valueToken));
                }

                return Expression.Call(
                    MethodCache["eq"],
                    new List<Expression>
                    {
                        left,
                        Expression.Constant(valueToken),
                        Expression.Constant(StringComparison.OrdinalIgnoreCase)
                    });
            }

            // Not Equal
            if (operatorToken.Equals("ne"))
            {
                int intValue;
                if (property.PropertyType == typeof(int) && int.TryParse(valueToken, out intValue))
                {
                    return Expression.NotEqual(left, Expression.Constant(intValue));
                }

                bool boolValue;
                if (property.PropertyType == typeof(bool) && bool.TryParse(valueToken, out boolValue))
                {
                    return Expression.NotEqual(left, Expression.Constant(boolValue));
                }
                
                if (property.PropertyType == typeof(DateTime))
                {
                    return Expression.NotEqual(left, Expression.Constant(ParseDateTime(valueToken)));
                }

                if (property.PropertyType != typeof(string))
                {
                    return Expression.NotEqual(left, Expression.Constant(valueToken));
                }

                return Expression.IsFalse(
                    Expression.Call(
                        MethodCache["eq"],
                        new List<Expression>
                        {
                            left,
                            Expression.Constant(valueToken),
                            Expression.Constant(StringComparison.OrdinalIgnoreCase)
                        }));
            }

            // Contains
            if (operatorToken.Equals("co"))
            {
                if (property.PropertyType != typeof(string))
                {
                    throw new InvalidOperationException("co only works on strings");
                }

                return Expression.Call(
                    MethodCache["co"],
                    new List<Expression>
                    {
                        left,
                        Expression.Constant(valueToken)
                    });
            }

            // Starts With
            if (operatorToken.Equals("sw"))
            {
                if (property.PropertyType != typeof (string))
                {
                    throw new InvalidOperationException("sw only works with strings");
                }

                return Expression.Call(
                    MethodCache["sw"],
                    new List<Expression>
                    {
                        left,
                        Expression.Constant(valueToken)
                    });
            }

            // Ends With
            if (operatorToken.Equals("ew"))
            {
                if (property.PropertyType != typeof (string))
                {
                    throw new InvalidOperationException("ew only works with strings");
                }

                return Expression.Call(
                    MethodCache["ew"],
                        new List<Expression>
                        {
                            left,
                            Expression.Constant(valueToken)
                        });
            }

            // Greater Than
            if (operatorToken.Equals("gt"))
            {
                int intValue;
                if (property.PropertyType == typeof(int) && int.TryParse(valueToken, out intValue))
                {
                    return Expression.GreaterThan(left, Expression.Constant(intValue));
                }

                bool boolValue;
                if (property.PropertyType == typeof(bool) && bool.TryParse(valueToken, out boolValue))
                {
                    return Expression.GreaterThan(left, Expression.Constant(boolValue));
                }
                
                if (property.PropertyType == typeof(DateTime))
                {
                    return Expression.GreaterThan(left, Expression.Constant(ParseDateTime(valueToken)));
                }
                
                if (property.PropertyType == typeof(string))
                {
                    var method = MethodCache["compareto"];
                    var result = Expression.Call(left, method, Expression.Constant(valueToken));
                    var zero = Expression.Constant(0);

                    return Expression.MakeBinary(ExpressionType.GreaterThan, result, zero);
                }

                return Expression.MakeBinary(ExpressionType.GreaterThan, left, Expression.Constant(valueToken));
            }

            // Greater Than or Equal
            if (operatorToken.Equals("ge"))
            {
                int intValue;
                if (property.PropertyType == typeof(int) && int.TryParse(valueToken, out intValue))
                {
                    return Expression.GreaterThanOrEqual(left, Expression.Constant(intValue));
                }

                bool boolValue;
                if (property.PropertyType == typeof(bool) && bool.TryParse(valueToken, out boolValue))
                {
                    return Expression.GreaterThanOrEqual(left, Expression.Constant(boolValue));
                }
                
                if (property.PropertyType == typeof(DateTime))
                {
                    return Expression.GreaterThanOrEqual(left, Expression.Constant(ParseDateTime(valueToken)));
                }
                
                if (property.PropertyType == typeof(string))
                {
                    var method = MethodCache["compareto"];
                    var result = Expression.Call(left, method, Expression.Constant(valueToken));
                    var zero = Expression.Constant(0);

                    return Expression.MakeBinary(ExpressionType.GreaterThanOrEqual, result, zero);
                }

                return Expression.MakeBinary(ExpressionType.GreaterThanOrEqual, left, Expression.Constant(valueToken));
            }

            // Less Than
            if (operatorToken.Equals("lt"))
            {
                int intValue;
                if (property.PropertyType == typeof(int) && int.TryParse(valueToken, out intValue))
                {
                    return Expression.LessThan(left, Expression.Constant(intValue));
                }

                bool boolValue;
                if (property.PropertyType == typeof(bool) && bool.TryParse(valueToken, out boolValue))
                {
                    return Expression.LessThan(left, Expression.Constant(boolValue));
                }
                
                if (property.PropertyType == typeof(DateTime))
                {
                    return Expression.LessThan(left, Expression.Constant(ParseDateTime(valueToken)));
                }

                if (property.PropertyType == typeof(string))
                {
                    var method = MethodCache["compareto"];
                    var result = Expression.Call(left, method, Expression.Constant(valueToken));
                    var zero = Expression.Constant(0);

                    return Expression.MakeBinary(ExpressionType.LessThan, result, zero);
                }

                return Expression.MakeBinary(ExpressionType.LessThan, left, Expression.Constant(valueToken));
            }

            // Less Than or Equal
            if (operatorToken.Equals("le"))
            {
                int intValue;
                if (property.PropertyType == typeof(int) && int.TryParse(valueToken, out intValue))
                {
                    return Expression.LessThanOrEqual(left, Expression.Constant(intValue));
                }

                bool boolValue;
                if (property.PropertyType == typeof(bool) && bool.TryParse(valueToken, out boolValue))
                {
                    return Expression.LessThanOrEqual(left, Expression.Constant(boolValue));
                }
                
                if (property.PropertyType == typeof(DateTime))
                {
                    return Expression.LessThanOrEqual(left, Expression.Constant(ParseDateTime(valueToken)));
                }

                if (property.PropertyType == typeof(string))
                {
                    var method = MethodCache["compareto"];
                    var result = Expression.Call(left, method, Expression.Constant(valueToken));
                    var zero = Expression.Constant(0);

                    return Expression.MakeBinary(ExpressionType.LessThanOrEqual, result, zero);
                }

                return Expression.MakeBinary(ExpressionType.LessThanOrEqual, left, Expression.Constant(valueToken));
            }

            throw new Exception("Invalid filter operator for a binary expression.");
        }
        
        protected static DateTime ParseDateTime(string valueToken)
        {
            return JToken.Parse("\"" + valueToken + "\"").ToObject<DateTime>(_JsonSerializer);
        }
        
        private static IDictionary<string, MethodInfo> CreateMethodCache()
        {
            var methodCache = new Dictionary<string, MethodInfo>();

            methodCache.Add("eq",
                typeof(string).GetMethod(
                    "Equals",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(string), typeof(StringComparison) },
                    new ParameterModifier[0]));
            methodCache.Add("compareto",
                typeof(string).GetMethod("CompareTo", new[] { typeof(string) }));
            methodCache.Add("any",
                typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(mi => mi.Name.Equals("Any") && mi.GetParameters().Length == 2));
            methodCache.Add("sw",
                typeof(FilterHelpers).GetMethod("StartsWith", BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(string) },
                    new ParameterModifier[0]));
            methodCache.Add("ew",
                typeof(FilterHelpers).GetMethod("EndsWith", BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(string) },
                    new ParameterModifier[0]));
            methodCache.Add("co",
                typeof(FilterHelpers).GetMethod("Contains", BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(string) },
                    new ParameterModifier[0]));
            methodCache.Add("pr",
                typeof (FilterHelpers).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(mi => mi.Name.Equals("IsPresent")));

            return methodCache;
        }
    }
}