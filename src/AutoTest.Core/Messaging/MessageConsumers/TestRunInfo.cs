using System;
using AutoTest.Core.Caching.Projects;
using System.Collections.Generic;
namespace AutoTest.Core.Messaging.MessageConsumers
{
	public class TestRunInfo
	{
		private List<string> _testsToRun;
		
		public Project Project { get; private set; }
		public string Assembly { get; private set; }
		public string[] TestsToRun { get { return _testsToRun.ToArray(); } }
		
		public TestRunInfo(Project project, string assembly)
		{
			Project = project;
			Assembly = assembly;
			_testsToRun = new List<string>();
		}
		
		public void AddTestsToRun(string[] tests)
		{
			_testsToRun.AddRange(tests);
		}
	}
}

