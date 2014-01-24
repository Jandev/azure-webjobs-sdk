﻿using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.WindowsAzure.Jobs.Test;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Microsoft.WindowsAzure.Jobs.Host.UnitTests.Engine.Runner.StaticBindings
{
    [TestClass]
    public class QueueInputParameterRuntimeBindingTests
    {
        [TestMethod]
        public void GetRouteParametersFromObjectParamType()
        {
            var names = QueueInputParameterRuntimeBinding.GetRouteParametersFromParamType(typeof(object));
            Assert.IsNull(names);
        }

        [TestMethod]
        public void GetRouteParametersFromIntParamType()
        {
            // Simple type, not structured, doesn't produce any route parameters.
            var names = QueueInputParameterRuntimeBinding.GetRouteParametersFromParamType(typeof(int));
            Assert.IsNull(names);
        }

        [TestMethod]
        public void GetRouteParametersFromStructuredParamType()
        {
            var names = QueueInputParameterRuntimeBinding.GetRouteParametersFromParamType(typeof(Complex));

            Array.Sort(names);
            var expected = new string[] { "IntProp", "StringProp" };
            AssertArrayEqual(expected, names);
        }

        [TestMethod]
        public void GetRouteParametersSimple()
        {
            // When JSON is a structured object, we can extract the fields as route parameters.
            string json = @"{ ""Name"" : 12, ""other"" : 13 }";
            var p = QueueInputParameterRuntimeBinding.GetRouteParameters(json, new string[] { "Name" });

            Assert.AreEqual(1, p.Count);
            Assert.AreEqual("12", p["Name"]);
        }

        [TestMethod]
        public void GetRouteParametersParseError()
        {
            // Test when payload is not a json object, does not produce any route parameters
            foreach (var json in new string[] { 
                "12",
                "[12]", 
                "x",
                "\\", 
                string.Empty,
                null
            })
            {
                var p = QueueInputParameterRuntimeBinding.GetRouteParameters(json, new string[] { "Name" });
                Assert.IsNull(p, "Shouldn't get route params from json:" + json);
            }
        }

        [TestMethod]
        public void GetRouteParametersComplexJson()
        {
            // 
            // When JSON is a structured object, we can extract the fields as route parameters.
            string json = @"{
""a"":1,
""b"":[1,2,3],
""c"":{}
}";
            var p = QueueInputParameterRuntimeBinding.GetRouteParameters(json, new string[] { "a", "b", "c" });

            // Only take simple types
            Assert.AreEqual(1, p.Count);
            Assert.AreEqual("1", p["a"]);
        }


        [TestMethod]
        public void GetRouteParametersDate()
        {
            // Dates with JSON can be tricky. Test Date serialization.
            DateTime date = new DateTime(1950, 6, 1, 2, 3, 30);

            var json = JsonConvert.SerializeObject(new { date = date });
            
            var p = QueueInputParameterRuntimeBinding.GetRouteParameters(json, new string[] { "date" });
                        
            Assert.AreEqual(1, p.Count);
            Assert.AreEqual(date.ToString(), p["date"]);
        }

        
        // Helper for comparing arrays
        static void AssertArrayEqual(string[] a, string[] b)
        {
            Assert.AreEqual(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i], b[i], ignoreCase: false);
            }
        }        

        // Type to test yielding route parameters.
        // Only simpl
        class Complex
        {
            public int _field; // skip: not a property

            public int IntProp { get; set; } // Yes
 
            public string StringProp { get; set; } // Yes
                        
            public Complex Nexted { get; set; } // skip: not simple type

            public static int StaticIntProp { get; set; } // skip: not instance property

            private int PrivateIntProp { get; set; } // skip: private property
        }
    }
}