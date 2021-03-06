﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutoTest.Core.FileSystem;
using NUnit.Framework;
using AutoTest.Core.Messaging.MessageConsumers;
using AutoTest.Core.Messaging;
using Rhino.Mocks;
using AutoTest.Core.Caching;
using AutoTest.Core.Caching.Projects;
using AutoTest.Core.Configuration;
using System.Reflection;
using AutoTest.Core.BuildRunners;
using AutoTest.Core.TestRunners;
using System.IO;
using AutoTest.Messages;
using AutoTest.Core.Caching.RunResultCache;

namespace AutoTest.Test.Core.Messaging.MessageConsumers
{
    [TestFixture]
    public class ProjectChangeConsumerTest
    {
        private ProjectChangeConsumer _consumer;
        private Project _project;
        private IMessageBus _bus;
        private IGenerateBuildList _listGenerator;
        private IConfiguration _configuration;
        private IBuildRunner _buildRunner;
        private ITestRunner _testRunner;
		private IDetermineIfAssemblyShouldBeTested _testAssemblyValidator;
		private IOptimizeBuildConfiguration _optimizer;
        private IPreProcessTestruns _preProcessor;
		private RunInfo _runInfo;
        private ILocateRemovedTests _removedTestLocator;

        [SetUp]
        public void SetUp()
        {
            _project = new Project(Path.GetFullPath("someProject.csproj"), new ProjectDocument(ProjectType.CSharp));
			_project.Value.SetOutputPath("");
			_project.Value.SetAssemblyName("someAssembly.dll");
            _bus = MockRepository.GenerateMock<IMessageBus>();
            _listGenerator = MockRepository.GenerateMock<IGenerateBuildList>();
            _configuration = MockRepository.GenerateMock<IConfiguration>();
            _buildRunner = MockRepository.GenerateMock<IBuildRunner>();
            _testRunner = MockRepository.GenerateMock<ITestRunner>();
			_testAssemblyValidator = MockRepository.GenerateMock<IDetermineIfAssemblyShouldBeTested>();
			_optimizer = MockRepository.GenerateMock<IOptimizeBuildConfiguration>();
			_runInfo = new RunInfo(_project);
			_runInfo.ShouldBuild();
			_runInfo.SetAssembly(_project.Value.AssemblyName);
			_optimizer.Stub(o => o.AssembleBuildConfiguration(null)).IgnoreArguments().Return(new RunInfo[] { _runInfo });
            _preProcessor = MockRepository.GenerateMock<IPreProcessTestruns>();
            _preProcessor.Stub(x => x.PreProcess(null)).IgnoreArguments().Return(new RunInfo[] { _runInfo });
            var preProcessors = new IPreProcessTestruns[] { _preProcessor };
            _removedTestLocator = MockRepository.GenerateMock<ILocateRemovedTests>();
            _consumer = new ProjectChangeConsumer(_bus, _listGenerator, _configuration, _buildRunner, new ITestRunner[] { _testRunner }, _testAssemblyValidator, _optimizer, preProcessors, _removedTestLocator);
        }

        [Test]
        public void Should_be_a_blocking_consumer()
        {
            _consumer.ShouldBeOfType<IBlockingConsumerOf<ProjectChangeMessage>>();
        }

        [Test]
        public void Should_publish_run_started_message()
        {
            _listGenerator.Stub(l => l.Generate(null)).IgnoreArguments().Return(new string[] { });

            _consumer.Consume(new ProjectChangeMessage());
            _bus.AssertWasCalled(b => b.Publish<RunStartedMessage>(null), b => b.IgnoreArguments());
        }

        [Test]
        public void Should_publish_run_finished_message()
        {
            _listGenerator.Stub(l => l.Generate(null)).IgnoreArguments().Return(new string[] {});

            _consumer.Consume(new ProjectChangeMessage());
            _bus.AssertWasCalled(b => b.Publish<RunFinishedMessage>(null), b => b.IgnoreArguments());
        }

        [Test]
        public void Should_run_builds()
        {
            var executable = Assembly.GetExecutingAssembly().Location;
            _listGenerator.Stub(l => l.Generate(null)).IgnoreArguments().Return(new string[] { "some file.csproj" });
            _configuration.Stub(c => c.BuildExecutable(_project.Value)).Return(executable);
            _buildRunner.Stub(b => b.RunBuild(_project, executable)).Return(new BuildRunResults(""));

            var message = new ProjectChangeMessage();
            message.AddFile(new ChangedFile("some file.csproj"));
            _consumer.Consume(message);
            _buildRunner.AssertWasCalled(b => b.RunBuild(_project, executable));
        }

        [Test]
        public void Should_not_run_builds_when_build_executable_not_defined()
        {
            var executable = Assembly.GetExecutingAssembly().Location;
            _listGenerator.Stub(l => l.Generate(null)).IgnoreArguments().Return(new string[] { "some file.csproj" });
            _configuration.Stub(c => c.BuildExecutable(_project.Value)).Return("non existing file");

            var message = new ProjectChangeMessage();
            message.AddFile(new ChangedFile("some file.csproj"));
            _consumer.Consume(message);
            _buildRunner.AssertWasNotCalled(b => b.RunBuild(_project, executable), b => b.IgnoreArguments());
        }

        [Test]
        public void Should_run_tests()
        {
            _project.Value.SetAsNUnitTestContainer();
            _project.Value.SetOutputPath("");
            _project.Value.SetAssemblyName("someProject.dll");
            _listGenerator.Stub(l => l.Generate(null)).IgnoreArguments().Return(new string[] { "some file.csproj" });
            _configuration.Stub(c => c.BuildExecutable(_project.Value)).Return("invalid_to_not_run_builds.exe");
            _testRunner.Stub(t => t.CanHandleTestFor(_project.Value)).Return(true);
            var result = new TestRunResults[] { new TestRunResults("", "", false, new TestResult[] { }) };
            _testRunner.Stub(t => t.RunTests(new TestRunInfo[] { new TestRunInfo(_project, "") })).IgnoreArguments()
                .Return(result);
            _removedTestLocator.Stub(r => r.SetRemovedTestsAsPassed(null, null)).IgnoreArguments().Return(result[0]);

            var message = new ProjectChangeMessage();
            message.AddFile(new ChangedFile("some file.csproj"));
            _consumer.Consume(message);
            _testRunner.AssertWasCalled(t => t.RunTests(new TestRunInfo[] { new TestRunInfo(null, "") }), t => t.IgnoreArguments());
        }
		
		[Test]
		public void Should_invalidate_test_assemblys()
		{
			_project.Value.SetAsNUnitTestContainer();
            _project.Value.SetOutputPath("");
            _project.Value.SetAssemblyName("someProject.dll");
            _listGenerator.Stub(l => l.Generate(null)).IgnoreArguments().Return(new string[] { "some file.csproj" });
            _configuration.Stub(c => c.BuildExecutable(_project.Value)).Return("invalid_to_not_run_builds.exe");
            _testRunner.Stub(t => t.CanHandleTestFor(_project.Value)).Return(true);
            _testRunner.Stub(t => t.RunTests(new TestRunInfo[] { new TestRunInfo(_project, "") })).IgnoreArguments()
                .Return(new TestRunResults[] { new TestRunResults("", "", false, new TestResult[] {}) });
			_testAssemblyValidator.Stub(t => t.ShouldNotTestAssembly("")).IgnoreArguments().Return(true);

            var message = new ProjectChangeMessage();
            message.AddFile(new ChangedFile("some file.csproj"));
            _consumer.Consume(message);
            _testRunner.AssertWasNotCalled(t => t.RunTests(new TestRunInfo[] { new TestRunInfo(null, "") }), t => t.IgnoreArguments());
		}

        [Test]
        public void Should_pre_process_run_information()
        {
            _project.Value.SetAsNUnitTestContainer();
            _project.Value.SetOutputPath("");
            _project.Value.SetAssemblyName("someProject.dll");
            _listGenerator.Stub(l => l.Generate(null)).IgnoreArguments().Return(new string[] { "some file.csproj" });
            _configuration.Stub(c => c.BuildExecutable(_project.Value)).Return("invalid_to_not_run_builds.exe");
            _testRunner.Stub(t => t.CanHandleTestFor(_project.Value)).Return(true);
            _testRunner.Stub(t => t.RunTests(new TestRunInfo[] { new TestRunInfo(_project, "") })).IgnoreArguments()
                .Return(new TestRunResults[] { new TestRunResults("", "", false, new TestResult[] { }) });
            _testAssemblyValidator.Stub(t => t.ShouldNotTestAssembly("")).IgnoreArguments().Return(true);

            var message = new ProjectChangeMessage();
            message.AddFile(new ChangedFile("some file.csproj"));
            _consumer.Consume(message);
            _preProcessor.AssertWasCalled(p => p.PreProcess(null), p => p.IgnoreArguments());
        }
		
		[Test]
		public void Should_rerun_test_if_pre_processor_says_so()
		{
			_project.Value.SetAsNUnitTestContainer();
            _project.Value.SetOutputPath("");
            _project.Value.SetAssemblyName("someProject.dll");
            _listGenerator.Stub(l => l.Generate(null)).IgnoreArguments().Return(new string[] { "some file.csproj" });
            _configuration.Stub(c => c.BuildExecutable(_project.Value)).Return("invalid_to_not_run_builds.exe");
            var result = new TestRunResults[] { new TestRunResults("", "", false, new TestResult[] {}) };
            _testRunner.Stub(t => t.CanHandleTestFor(_project.Value)).Return(true);
            _testRunner.Stub(t => t.RunTests(new TestRunInfo[] { new TestRunInfo(_project, "") })).IgnoreArguments()
                .Return(result);
			_runInfo.ShouldRerunAllTestWhenFinishedFor(TestRunner.Any);
            _removedTestLocator.Stub(r => r.SetRemovedTestsAsPassed(null, null)).IgnoreArguments().Return(result[0]);

            var message = new ProjectChangeMessage();
            message.AddFile(new ChangedFile("some file.csproj"));
            _consumer.Consume(message);
            _testRunner.AssertWasCalled(t => t.RunTests(new TestRunInfo[] { new TestRunInfo(null, "") }), t => t.IgnoreArguments().Repeat.Twice());
		}
    }
}
