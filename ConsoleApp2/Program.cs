using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp2
{
    public interface ICloningService
    {
        T Clone<T>(T source);
    }

    public class CloningService : ICloningService
    {
        Dictionary<string, Stopwatch> m_stopwatches = new Dictionary<string, Stopwatch>();

        HashSet<Type> m_parameterlessCtor = new HashSet<Type>();
        Dictionary<Type, IChild[]> m_childs = new Dictionary<Type, IChild[]>();

        public CloningService()
        {
            m_stopwatches.Add("RequiresShallowClone", new Stopwatch());
            m_stopwatches.Add("HasParameterlessConstructor", new Stopwatch());
            m_stopwatches.Add("GetObjectChilds", new Stopwatch());
            m_stopwatches.Add("GetArrayItems", new Stopwatch());
            m_stopwatches.Add("ImplementsGenericInterface", new Stopwatch());
        }

        public T Clone<T>(T source)
        {
            if (source == null)
            {
                return default(T);
            }

            if (RequiresShallowClone(source))
            {
                return source;
            }

            return (T)DeepClone(source);
        }

        // Feel free to add any other methods, classes, etc.

        private bool RequiresShallowClone(object source)
        {
            var stw = m_stopwatches["RequiresShallowClone"];
            stw.Start();

            var result =
                source == null ||
                source is string ||
                source is bool ||
                source is char ||
                source is int ||
                source is long ||
                source is double ||
                source.GetType() == typeof(object);

            stw.Stop();

            return result;
        }

        private bool RequiresShallowClone(IChild child, object value)
        {
            return child.RequiresShallowClone || RequiresShallowClone(value);
        }

        private bool HasParameterlessConstructor(Type type)
        {
            var stw = m_stopwatches["HasParameterlessConstructor"];
            stw.Start();

            var res =
                type.IsValueType ||
                m_parameterlessCtor.Contains(type) ||
                type.GetConstructor(Type.EmptyTypes) != null;

            m_parameterlessCtor.Add(type);
            stw.Stop();
            return res;
        }

        private IChild[] GetObjectChilds(Type type, object obj)
        {
            var stw = m_stopwatches["GetObjectChilds"];
            stw.Start();

            var props = type.GetProperties();
            var fields = type.GetFields();
            var lst = new List<IChild>();

            if (m_childs.ContainsKey(type))
            {
                lst.AddRange(m_childs[type]);
            }
            else
            {
                foreach (var prop in props)
                {
                    if (prop.CanRead && prop.GetGetMethod().IsPublic &&
                        prop.CanWrite && prop.GetSetMethod().IsPublic &&
                        prop.Name != "Item" &&
                        prop.GetCustomAttribute<CloneableAttribute>()?.Mode != CloningMode.Ignore)
                    {
                        lst.Add(new Property(prop));
                    }
                }

                foreach (var field in fields)
                {
                    if (field.IsPublic)
                    {
                        lst.Add(new Field(field));
                    }
                }

                m_childs[type] = lst.ToArray();
            }

            if (ImplementsGenericInterface(type, typeof(ICollection<>)))
            {
                foreach (var item in (IEnumerable)obj)
                {
                    lst.Add(new CollectionItem(item));
                }
            }

            stw.Stop();

            return lst.ToArray();
        }

        private IChild[] GetArrayItems(int size)
        {
            var stw = m_stopwatches["GetArrayItems"];
            stw.Start();

            var arr = new IChild[size];
            for (var i = 0; i < arr.Length; i++)
            {
                arr[i] = new ArrayItem(i);
            }

            stw.Stop();

            return arr;
        }

        private bool ImplementsGenericInterface(Type type, Type interfaceType)
        {
            var stw = m_stopwatches["ImplementsGenericInterface"];
            stw.Start();
            foreach (var i in type.GetInterfaces())
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == interfaceType)
                {
                    return true;
                }
            }

            stw.Stop();
            return false;
        }

        /// <summary>
        /// I decided to not use recursion to avoid stack overflow, what if object graph is too large?
        /// </summary>
        /// <param name="source"></param>
        /// <returns></returns>
        private object DeepClone(object source)
        {
            var state = CloneState.NextBreadcrumb;

            var value = source;

            var breadcrumbs = new Stack<Breadcrumb>();

            Breadcrumb bcb = null;

            IChild child = null;

            Type type = null;

            // to avoid cyclic references, if objects already exists in target tree - use it
            var objectMap = new Hashtable();
            
            switch (state)
            {
                case CloneState.NextChild:
                    if (bcb.Childs.Length <= bcb.ChildIdx)
                    {
                        if (breadcrumbs.Count == 1)
                        {
                            return bcb.TargetObject;
                        }
                        else
                        {
                            breadcrumbs.Pop();
                            var mappedValue = objectMap[bcb.SourceObject] = bcb.TargetObject;
                            bcb = breadcrumbs.Peek();
                            child = bcb.Childs[bcb.ChildIdx];
                            child.SetValue(bcb.TargetObject, mappedValue);
                            bcb.ChildIdx++;
                            goto case CloneState.NextChild;
                        }
                    }

                    child = bcb.Childs[bcb.ChildIdx];
                    value = child.GetValue(bcb.SourceObject);

                    if (value == null || RequiresShallowClone(child, value))
                    {
                        child.SetValue(bcb.TargetObject, value);
                        bcb.ChildIdx++;
                        goto case CloneState.NextChild;
                    }
                    else if (objectMap.ContainsKey(value))
                    {
                        child.SetValue(bcb.TargetObject, objectMap[value]);
                        bcb.ChildIdx++;
                        goto case CloneState.NextChild;
                    }
                    else
                    {
                        goto case CloneState.NextBreadcrumb;
                    }

                case CloneState.NextBreadcrumb:
                    type = value.GetType();

                    if (type.IsArray)
                    {
                        var size = ((Array)value).Length;
                        bcb = new Breadcrumb
                        {
                            Childs = GetArrayItems(size),
                            TargetObject = Array.CreateInstance(type.GetElementType(), size),
                        };
                    }
                    else if (HasParameterlessConstructor(type))
                    {
                        bcb = new Breadcrumb
                        {
                            TargetObject = Activator.CreateInstance(type),
                            Childs = GetObjectChilds(type, value),
                        };
                    }
                    else
                    {
                        // ignore
                        if (bcb != null)
                        {
                            child.SetValue(bcb.TargetObject, null);
                            bcb.ChildIdx++;
                            goto case CloneState.NextChild;
                        }
                        else
                        {
                            throw new NotSupportedException(
                                "cloning type schould have parameterless constructor or be a one-dimensional array");
                        }
                    }

                    bcb.SourceObject = value;
                    bcb.ChildIdx = 0;
                    objectMap.Add(value, bcb.TargetObject);
                    breadcrumbs.Push(bcb);
                    goto case CloneState.NextChild;
            }

            return source;
        }

        enum CloneState
        {
            NextBreadcrumb,
            NextChild,
        }

        class Breadcrumb
        {
            public object SourceObject;
            public object TargetObject;
            public IChild[] Childs;
            public int ChildIdx;
        }

        interface IChild
        {
            object GetValue(object obj);
            void SetValue(object obj, object value);
            bool RequiresShallowClone { get; }
        }

        class Property : IChild
        {
            readonly PropertyInfo m_prop;

            public Property(PropertyInfo prop)
            {
                m_prop = prop;
                RequiresShallowClone = m_prop.GetCustomAttribute<CloneableAttribute>()?.Mode == CloningMode.Shallow;
            }

            public void SetValue(object obj, object value)
            {
                m_prop.SetValue(obj, value);
            }

            public object GetValue(object obj)
            {
                return m_prop.GetValue(obj);
            }

            public bool RequiresShallowClone { get; }
        }

        class Field : IChild
        {
            readonly FieldInfo m_field;

            public Field(FieldInfo field)
            {
                m_field = field;
                RequiresShallowClone =
                    m_field.GetCustomAttribute<CloneableAttribute>()?.Mode == CloningMode.Shallow;
            }

            public void SetValue(object obj, object value)
            {
                m_field.SetValue(obj, value);
            }

            public bool RequiresShallowClone { get; }

            public object GetValue(object obj)
            {
                return m_field.GetValue(obj);
            }
        }

        class ArrayItem : IChild
        {
            readonly int m_index;

            public ArrayItem(int index)
            {
                m_index = index;
            }

            public void SetValue(object obj, object value)
            {
                ((Array)obj).SetValue(value, m_index);
            }

            public object GetValue(object obj)
            {
                return ((Array)obj).GetValue(m_index);
            }

            public bool RequiresShallowClone
            {
                get
                {
                    return false;
                }
            }
        }

        class CollectionItem : IChild
        {
            readonly object m_value;

            public CollectionItem(object value)
            {
                m_value = value;
            }

            public void SetValue(/*ICollection<>*/object obj, object value)
            {
                (obj.GetType()).GetMethod("Add").Invoke(obj, new[] { value });
            }

            public object GetValue(object obj)
            {
                return m_value;
            }

            public bool RequiresShallowClone
            {
                get
                {
                    return false;
                }
            }
        }
    }

    public enum CloningMode
    {
        Deep = 0,
        Shallow = 1,
        Ignore = 2,
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class CloneableAttribute : Attribute
    {
        public CloningMode Mode { get; }

        public CloneableAttribute(CloningMode mode)
        {
            Mode = mode;
        }
    }

    //    class Program
    //    {
    //        class Foo
    //        {
    //            public Bar[] Bar { get; set; }
    //        }

    //        struct Bar
    //        {
    //            public int Number { get; set; }
    //            public string Text { get; set; }
    //            public object Obj { get; set; }

    //            [Cloneable(CloningMode.Ignore)]
    //            public A A { get; set; }
    //        }

    //        class A
    //        {
    //            public double Double { get; set; }
    //            public bool Flag { get; set; }
    //            public List<int> Numbers { get; set; }
    //        }

    //        static void Main(string[] args)
    //        {
    //            var source = new Foo {
    //                Bar = new[] {
    //                    new Bar { Number = 134, Text = "Hello", Obj = null, A = default(A), },
    //                    new Bar { Number = 34534, Text = "World", Obj = new object(),
    //                        A = new A {Double = 0.567, Flag = true, Numbers = new List<int> { 7, 9, 8 } } } },
    //            };

    //            var res = new CloningService().Clone(source);

    //            Console.WriteLine(res.Bar);

    //            Console.ReadKey();
    //        }
    //    }
    //}

    public class CloningServiceTest
    {
        public class Simple
        {
            public int I;
            public string S { get; set; }
            [Cloneable(CloningMode.Ignore)]
            public string Ignored { get; set; }
            [Cloneable(CloningMode.Shallow)]
            public object Shallow { get; set; }

            public virtual string Computed => S + I + Shallow;
        }

        public struct SimpleStruct
        {
            public int I;
            public string S { get; set; }
            [Cloneable(CloningMode.Ignore)]
            public string Ignored { get; set; }

            public string Computed => S + I;

            public SimpleStruct(int i, string s)
            {
                I = i;
                S = s;
                Ignored = null;
            }
        }

        public class Simple2 : Simple
        {
            public double D;
            public SimpleStruct SS;
            public override string Computed => S + I + D + SS.Computed;
        }

        public class Node
        {
            public Node Left;
            public Node Right;
            public object Value;
            public int TotalNodeCount =>
                1 + (Left?.TotalNodeCount ?? 0) + (Right?.TotalNodeCount ?? 0);
        }

        public ICloningService Cloner = new CloningService();
        public Action[] AllTests => new Action[] {
            SimpleTest,
            SimpleStructTest,
            Simple2Test,
            NodeTest,
            ArrayTest,
            CollectionTest,
            ArrayTest2,
            CollectionTest2,
            MixedCollectionTest,
            RecursionTest,
            RecursionTest2,
            PerformanceTest,
        };

        public static void Assert(bool criteria)
        {
            if (!criteria)
                throw new InvalidOperationException("Assertion failed.");
        }

        public void Measure(string title, Action test)
        {
            test(); // Warmup
            var sw = new Stopwatch();
            GC.Collect();
            sw.Start();
            test();
            sw.Stop();
            Console.WriteLine($"{title}: {sw.Elapsed.TotalMilliseconds:0.000}ms");
        }

        public void SimpleTest()
        {
            var s = new Simple() { I = 1, S = "2", Ignored = "3", Shallow = new object() };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(s.Computed == c.Computed);
            Assert(c.Ignored == null);
            Assert(ReferenceEquals(s.Shallow, c.Shallow));
        }

        public void SimpleStructTest()
        {
            var s = new SimpleStruct(1, "2") { Ignored = "3" };
            var c = Cloner.Clone(s);
            Assert(s.Computed == c.Computed);
            Assert(c.Ignored == null);
        }

        public void Simple2Test()
        {
            var s = new Simple2()
            {
                I = 1,
                S = "2",
                D = 3,
                SS = new SimpleStruct(3, "4"),
            };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(s.Computed == c.Computed);
        }

        public void NodeTest()
        {
            var s = new Node
            {
                Left = new Node
                {
                    Right = new Node()
                },
                Right = new Node()
            };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(s.TotalNodeCount == c.TotalNodeCount);
        }

        public void RecursionTest()
        {
            var s = new Node();
            s.Left = s;
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(null == c.Right);
            Assert(c == c.Left);
        }

        public void ArrayTest()
        {
            var n = new Node
            {
                Left = new Node
                {
                    Right = new Node()
                },
                Right = new Node()
            };
            var s = new[] { n, n };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(s.Sum(n1 => n1.TotalNodeCount) == c.Sum(n1 => n1.TotalNodeCount));
            Assert(c[0] == c[1]);
        }

        public void CollectionTest()
        {
            var n = new Node
            {
                Left = new Node
                {
                    Right = new Node()
                },
                Right = new Node()
            };
            var s = new List<Node>() { n, n };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(s.Sum(n1 => n1.TotalNodeCount) == c.Sum(n1 => n1.TotalNodeCount));
            Assert(c[0] == c[1]);
        }

        public void ArrayTest2()
        {
            var s = new[] { new[] { 1, 2, 3 }, new[] { 4, 5 } };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(15 == c.SelectMany(a => a).Sum());
        }

        public void CollectionTest2()
        {
            var s = new List<List<int>> { new List<int> { 1, 2, 3 }, new List<int> { 4, 5 } };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(15 == c.SelectMany(a => a).Sum());
        }

        public void MixedCollectionTest()
        {
            var s = new List<IEnumerable<int[]>> {
                new List<int[]> {new [] {1}},
                new List<int[]> {new [] {2, 3}},
            };
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(6 == c.SelectMany(a => a.SelectMany(b => b)).Sum());
        }

        public void RecursionTest2()
        {
            var l = new List<Node>();
            var n = new Node { Value = l };
            n.Left = n;
            l.Add(n);
            var s = new object[] { null, l, n };
            s[0] = s;
            var c = Cloner.Clone(s);
            Assert(s != c);
            Assert(c[0] == c);
            var cl = (List<Node>)c[1];
            Assert(l != cl);
            var cn = cl[0];
            Assert(n != cn);
            Assert(cl == cn.Value);
            Assert(cn.Left == cn);
        }

        public void PerformanceTest()
        {
            Func<int, Node> makeTree = null;
            makeTree = depth => {
                if (depth == 0)
                    return null;
                return new Node
                {
                    Value = depth,
                    Left = makeTree(depth - 1),
                    Right = makeTree(depth - 1),
                };
            };
            for (var i = 10; i <= 20; i++)
            {
                var root = makeTree(i);
                Measure($"Cloning {root.TotalNodeCount} nodes", () => {
                    var copy = Cloner.Clone(root);
                    Assert(root != copy);
                });
            }
        }

        public void RunAllTests()
        {
            foreach (var test in AllTests)
                test.Invoke();
            Console.WriteLine("Done.");
        }
    }

    public class Solution
    {
        public static void Main(string[] args)
        {
            var cloningServiceTest = new CloningServiceTest();
            var allTests = cloningServiceTest.AllTests;
            foreach (var test in allTests.Skip(11))
            {
                try
                {
                    test.Invoke();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed on {test.GetMethodInfo().Name}.");
                }
            }
            Console.WriteLine("Done.");
        }
    }
}
