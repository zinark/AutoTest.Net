﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutoTest.Core.Messaging;
using AutoTest.Core.BuildRunners;
using AutoTest.Core.Configuration;
using AutoTest.Core.Caching;
using AutoTest.Core.Caching.Projects;
using AutoTest.Core.TestRunners;
using System.IO;
using AutoTest.Core.TestRunners.TestRunners;
using Castle.Core.Logging;
using AutoTest.Core.DebugLog;
using AutoTest.Messages;

namespace AutoTest.Core.Messaging.MessageConsumers
{
    class ProjectChangeConsumer : IBlockingConsumerOf<ProjectChangeMessage>
    {
        private IMessageBus _bus;
        private IGenerateBuildList _listGenerator;
        private IConfiguration _configuration;
        private IBuildRunner _buildRunner;
        private ITestRunner[] _testRunners;
		private IDetermineIfAssemblyShouldBeTested _testAssemblyValidator;
		private IOptimizeBuildConfiguration _buildOptimizer;
        private IPreProcessTestruns[] _preProcessors;
        private ILocateRemovedTests _removedTestLocator;

        public ProjectChangeConsumer(IMessageBus bus, IGenerateBuildList listGenerator, IConfiguration configuration, IBuildRunner buildRunner, ITestRunner[] testRunners, IDetermineIfAssemblyShouldBeTested testAssemblyValidator, IOptimizeBuildConfiguration buildOptimizer, IPreProcessTestruns[] preProcessors, ILocateRemovedTests removedTestLocator)
        {
            _bus = bus;
            _listGenerator = listGenerator;
            _configuration = configuration;
            _buildRunner = buildRunner;
            _testRunners = testRunners;
			_testAssemblyValidator = testAssemblyValidator;
			_buildOptimizer = buildOptimizer;
            _preProcessors = preProcessors;
            _removedTestLocator = removedTestLocator;
        }

        #region IConsumerOf<ProjectChangeMessage> Members

        public void Consume(ProjectChangeMessage message)
        {
            Debug.ConsumingProjectChangeMessage(message);
            _bus.Publish(new RunStartedMessage(message.Files));
            var runReport = execute(message);
            _bus.Publish(new RunFinishedMessage(runReport));
        }

        private RunReport execute(ProjectChangeMessage message)
        {
            var runReport = new RunReport();
            var projectsAndDependencies = _listGenerator.Generate(getListOfChangedProjects(message));
			var list = _buildOptimizer.AssembleBuildConfiguration(projectsAndDependencies);
            if (!buildAll(list, runReport))
				return runReport;
            markAllAsBuilt(list);
			testAll(list, runReport);
            return runReport;
        }

        private void markAllAsBuilt(RunInfo[] list)
        {
            foreach (var info in list)
                info.Project.Value.HasBeenBuilt();
        }

        private string[] getListOfChangedProjects(ProjectChangeMessage message)
        {
            var projects = new List<string>();
            foreach (var file in message.Files)
                projects.Add(file.FullName);
            return projects.ToArray();
        }
		
		private bool buildAll(RunInfo[] projectList, RunReport runReport)
		{
			var indirectlyBuild = new List<string>();
			foreach (var file in projectList)
            {
				if (file.ShouldBeBuilt)
				{
					Debug.WriteMessage(string.Format("Set to build project {0}", file.Project.Key));
	                if (!build(file, runReport))
	                    return false;
				}
				else
				{
					Debug.WriteMessage(string.Format("Not set to build project {0}", file.Project.Key));
					indirectlyBuild.Add(file.Project.Key);
				}
            }
			foreach (var project in indirectlyBuild)
				runReport.AddBuild(project, new TimeSpan(0), true);
			return true;
		}
		
		private void testAll(RunInfo[] projectList, RunReport runReport)
		{
            projectList = preProcessTestRun(projectList);
            runPreProcessedTestRun(projectList, runReport);
		}
		
		private void runPreProcessedTestRun(RunInfo[] projectList, RunReport runReport)
		{
			foreach (var runner in _testRunners)
            {
				var runInfos = new List<TestRunInfo>();
				foreach (var file in projectList)
	            {
					var project = file.Project;
					
					if (hasInvalidAssembly(file))
						continue;
	            	var assembly = file.Assembly;
					
					if (_testAssemblyValidator.ShouldNotTestAssembly(assembly))
					    continue;
					if (!project.Value.ContainsTests)
	                	continue;
                    if (runner.CanHandleTestFor(project.Value))
                        runInfos.Add(file.CloneToTestRunInfo());
					_bus.Publish(new RunInformationMessage(InformationType.TestRun, project.Key, assembly, runner.GetType()));
				}
				if (runInfos.Count > 0)
				{
					runTests(runner, runInfos.ToArray(), runReport);
					
					var rerunInfos = new List<TestRunInfo>();
					foreach (var info in runInfos)
					{
						if (info.RerunAllTestWhenFinishedForAny())
							rerunInfos.Add(new TestRunInfo(info.Project, info.Assembly));
					}
					if (rerunInfos.Count > 0)
						runTests(runner, rerunInfos.ToArray(), runReport);
				}
				
			}
		}

        private RunInfo[] preProcessTestRun(RunInfo[] runInfos)
        {
            foreach (var preProcessor in _preProcessors)
                runInfos = preProcessor.PreProcess(runInfos);
            return runInfos;
        }
		
		private bool hasInvalidOutputPath(RunInfo info)
		{
			return info.Assembly == null;
		}
		
		private bool hasInvalidAssembly(RunInfo info)
		{
			if (info.Assembly == null)
			{
				_bus.Publish(new ErrorMessage(string.Format("Assembly was unexpectedly set to null for {0}. Skipping assembly", info.Project.Key)));
				return true;
			}
			return false;
		}

        private bool build(RunInfo info, RunReport runReport)
        {
            if (File.Exists(_configuration.BuildExecutable(info.Project.Value)))
            {
                _bus.Publish(new RunInformationMessage(
                                 InformationType.Build,
                                 info.Project.Key,
                                 info.Assembly,
                                 typeof(MSBuildRunner)));
                if (!buildProject(info.Project, runReport))
                    return false;
            }

            return true;
        }

        private bool buildProject(Project project, RunReport report)
        {
            var buildReport = _buildRunner.RunBuild(project, _configuration.BuildExecutable(project.Value));
            var succeeded = buildReport.ErrorCount == 0;
            report.AddBuild(buildReport.Project, buildReport.TimeSpent, succeeded);
            _bus.Publish(new BuildRunMessage(buildReport));
            return succeeded;
        }

        #endregion

        private void runTests(ITestRunner testRunner, TestRunInfo[] runInfos, RunReport runReport)
        {
            var results = testRunner.RunTests(runInfos);
            var modifiedResults = new List<TestRunResults>();
			foreach (var result in results)
			{
                var modified = _removedTestLocator.SetRemovedTestsAsPassed(result, runInfos);
	            runReport.AddTestRun(
                    modified.Project,
                    modified.Assembly,
                    modified.TimeSpent,
                    modified.Passed.Length,
                    modified.Ignored.Length,
                    modified.Failed.Length);
                _bus.Publish(new TestRunMessage(modified));
                modifiedResults.Add(modified);
			}
			informPreProcessor(modifiedResults.ToArray());
        }
		
        private void informPreProcessor(TestRunResults[] results)
        {
            foreach (var preProcess in _preProcessors)
                preProcess.RunFinished(results);
        }
    }
}
