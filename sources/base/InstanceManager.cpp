/* InstanceManager.cpp
*
*  MIT License
*
*  Copyright (c) 2020-2026 awawa-dev
*
*  Project homesite: https://github.com/awawa-dev/HyperHDR
*
*  Permission is hereby granted, free of charge, to any person obtaining a copy
*  of this software and associated documentation files (the "Software"), to deal
*  in the Software without restriction, including without limitation the rights
*  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
*  copies of the Software, and to permit persons to whom the Software is
*  furnished to do so, subject to the following conditions:
*
*  The above copyright notice and this permission notice shall be included in all
*  copies or substantial portions of the Software.

*  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
*  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
*  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
*  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
*  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
*  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
*  SOFTWARE.
*/

#ifndef PCH_ENABLED
	#include <QThread>
	#include <utility>
#endif

#include <QStringList>

#include <base/InstanceManager.h>
#include <base/LightingInstance.h>
#include <db/InstanceTable.h>
#include <base/GrabberWrapper.h>
#include <base/AccessManager.h>
#include <base/Muxer.h>
#include <utils/GlobalSignals.h>
#include <lut-calibrator/LutCalibrator.h>

QString InstanceManager::getRootPath()
{
	return _rootPath;
}

InstanceManager::InstanceManager(const QString& rootPath)
	: _log("HYPER_MANAGER")
	, _instanceTable(new InstanceTable())
	, _rootPath(rootPath)
	, _fireStarter(0)
	, _videoBenchmark(this)
{
	qRegisterMetaType<InstanceState>("InstanceState");
	connect(this, &InstanceManager::SignalInstanceStateChanged, this, &InstanceManager::handleInstanceStateChange);

	connect(&_videoBenchmark, &VideoBenchmark::SignalBenchmarkUpdate, this, &InstanceManager::SignalBenchmarkUpdate);
	connect(this, &InstanceManager::SignalBenchmarkCapture, &_videoBenchmark, &VideoBenchmark::benchmarkCapture);

	connect(GlobalSignals::getInstance(), &GlobalSignals::SignalRequestComponent, this, &InstanceManager::handleRequestComponent);
	connect(GlobalSignals::getInstance(), &GlobalSignals::SignalLutRequest, this, &InstanceManager::handleLutRequestSignal);
}

void InstanceManager::handleRequestComponent(overglow::Components component, int instanceIndex, bool listen)
{
	if (component == overglow::Components::COMP_VIDEOGRABBER && instanceIndex == -1)
	{
		Warning(_log, "Global request to {:s} USB grabber", (listen) ? "resume" : "pause");
		toggleGrabbersAllInstances(listen, false, true);
	}
}

InstanceManager::~InstanceManager()
{
	Debug(_log, "InstanceManager has been removed");
}

void InstanceManager::handleLutRequestSignal()
{
	static bool requested = false;

	if (!requested)
	{
		requested = true;

		LutCalibrator::CreateDefaultLut(_rootPath);
	}
}

void InstanceManager::startAll(bool disableOnStartup)
{
	auto instanceList = _instanceTable->getAllInstances(true);

	_fireStarter = instanceList.count();

	for (const auto& entry : instanceList)
	{
		startInstance(entry["instance"].toInt(), nullptr, 0, disableOnStartup);
	}
}

void InstanceManager::stopAllonExit()
{
	disconnect(this, nullptr, nullptr, nullptr);

	Warning(_log, "Running instances: {:d}, starting instances: {:d}"
		, (int)_runningInstances.size()
		, (int)_startingInstances.size());

	_runningInstances.clear();
	_startingInstances.clear();

	while (LightingInstance::getTotalRunningCount())
	{
		Warning(_log, "Waiting for instances: {:d}", (int)LightingInstance::getTotalRunningCount());
		QThread::msleep(25);
	}
	Info(_log, "All instances are closed now");
}

void InstanceManager::handleInstanceStateChange(InstanceState state, quint8 instance, const QString& name)
{
	switch (state)
	{
		case InstanceState::START:
			emit GlobalSignals::getInstance()->SignalPerformanceStateChanged(true, PerformanceReportType::INSTANCE, instance, name);
			break;
		case InstanceState::STOP:
			emit GlobalSignals::getInstance()->SignalPerformanceStateChanged(false, PerformanceReportType::INSTANCE, instance);
			emit GlobalSignals::getInstance()->SignalPerformanceStateChanged(false, PerformanceReportType::LED, instance);
			break;
		default:
			break;
	}
}


std::shared_ptr<LightingInstance> InstanceManager::getLightingInstance(quint8 instance)
{
	if (_runningInstances.contains(instance))
		return _runningInstances.value(instance);

	Warning(_log, "The requested instance index '{:d}' with name '{:s}' isn't running, return main instance", instance, (_instanceTable->getNamebyIndex(instance)));
	return _runningInstances.value(0);
}

std::vector<QString> InstanceManager::getInstances()
{
	std::vector<QString> ret;

	const auto keys = _runningInstances.keys();
	for (const quint8& key : keys)
	{
		ret.push_back(QString::number(key));
		ret.push_back(_instanceTable->getNamebyIndex(key));
	}

	return ret;
}

void InstanceManager::setInstanceColor(int instance, int /*priority*/, ColorRgb ledColors, int /*timeout_ms*/)
{
	QVector<ColorRgb> rgbColor{ ledColors };

	for (const auto& selInstance : std::as_const(_runningInstances))
		if (instance == -1 || selInstance->getInstanceIndex() == instance)
		{
			QUEUE_CALL_3(selInstance.get(), setColor, int, 1, QVector<ColorRgb>, rgbColor, int, 0);
		}
}

void  InstanceManager::setInstanceEffect(int instance, QString effectName, int /*priority*/)
{
	for (const auto& selInstance : std::as_const(_runningInstances))
		if (instance == -1 || selInstance->getInstanceIndex() == instance)
		{
			QUEUE_CALL_2(selInstance.get(), setEffect, QString, effectName, int, 1);
		}
}

void InstanceManager::clearInstancePriority(int instance, int /*priority*/)
{
	for (const auto& selInstance : std::as_const(_runningInstances))
		if (instance == -1 || selInstance->getInstanceIndex() == instance)
		{
			QUEUE_CALL_1(selInstance.get(), clear, int, 1);
		}
}

std::list<EffectDefinition> InstanceManager::getEffects()
{
	std::list<EffectDefinition> efxs;

	if (IsInstanceRunning(0))
	{
		auto inst = getLightingInstance(0);

		SAFE_CALL_0_RET(inst.get(), getEffects, std::list<EffectDefinition>, efxs);
	}

	return efxs;
}

QVector<QVariantMap> InstanceManager::getInstanceData() const
{
	QVector<QVariantMap> instances = _instanceTable->getAllInstances();
	for (auto& entry : instances)
	{
		// add running state
		entry["running"] = _runningInstances.contains(entry["instance"].toInt());
	}
	return instances;
}

bool InstanceManager::areInstancesReady()
{
	if (_fireStarter <= 0)
		return false;

	return (--_fireStarter == 0);
}

void InstanceManager::setSmoothing(int time)
{
	for (const auto& instance : std::as_const(_runningInstances))
		QUEUE_CALL_1(instance.get(), setSmoothing, int, time);
}

QJsonObject InstanceManager::getAverageColor(quint8 index)
{
	LightingInstance* instance = InstanceManager::getLightingInstance(index).get();
	QJsonObject res;

	SAFE_CALL_0_RET(instance, getAverageColor, QJsonObject, res);

	return res;
}

void InstanceManager::toggleStateAllInstances(bool pause)
{
	for (const auto& instance : std::as_const(_runningInstances))
	{
		emit instance->SignalRequestComponent(overglow::COMP_ALL, pause);
	}
}

void InstanceManager::toggleGrabbersAllInstances(bool pause, bool includingSystemGrabber, bool includingVideoGrabber)
{
	for (const auto& instance : std::as_const(_runningInstances))
	{
		QUEUE_CALL_3(instance.get(), turnGrabbers, bool, pause, bool, includingSystemGrabber, bool, includingVideoGrabber);
	}
}

void InstanceManager::hibernate(bool wakeUp, overglow::SystemComponent source)
{
	if (source == overglow::SystemComponent::LOCKER || source == overglow::SystemComponent::MONITOR)
	{
		Debug(_log, "OS event: {:s}", (source == overglow::SystemComponent::LOCKER) ? ((wakeUp) ? "OS unlocked" : "OS locked") : ((wakeUp) ? "Monitor On" : "Monitor Off"));

		if (wakeUp)
			QTimer::singleShot(3000, this, [this, wakeUp]() { toggleGrabbersAllInstances(wakeUp, true, false);  });
		else
			toggleGrabbersAllInstances(wakeUp, true, false);
	}
	else if (source == overglow::SystemComponent::SUSPEND)
	{
		if (!wakeUp)
		{
			Warning(_log, "The system is going to sleep");
			toggleStateAllInstances(false);
		}
		else
		{
			Warning(_log, "The system is going to wake up");
			QTimer::singleShot(3000, this, [this]() { toggleStateAllInstances(true);  });
		}
	}
}

bool InstanceManager::startInstance(quint8 inst, QObject* caller, int tan, bool disableOnStartup)
{
	if (_instanceTable->instanceExist(inst))
	{
		if (!_runningInstances.contains(inst) && !_startingInstances.contains(inst))
		{
			QThread* instanceThread = new QThread();
			instanceThread->setObjectName("InstanceThread");

			auto lightingInstance = std::shared_ptr<LightingInstance>(
				new LightingInstance(inst,
					disableOnStartup,
					_instanceTable->getNamebyIndex(inst)),
				[](LightingInstance* oldInstance) {
					THREAD_REMOVER(QString("Overglow instance at index = %1").arg(oldInstance->getInstanceIndex()),
						oldInstance->thread(), oldInstance);
				}
			);

			lightingInstance->moveToThread(instanceThread);

			connect(instanceThread, &QThread::started, lightingInstance.get(), &LightingInstance::start);
			connect(lightingInstance.get(), &LightingInstance::SignalInstanceJustStarted, this, &InstanceManager::handleInstanceJustStarted);
			connect(lightingInstance.get(), &LightingInstance::SignalInstancePauseChanged, this, &InstanceManager::SignalInstancePauseChanged);
			connect(lightingInstance.get(), &LightingInstance::SignalInstanceSettingsChanged, this, &InstanceManager::SignalSettingsChanged);
			connect(lightingInstance.get(), &LightingInstance::SignalRequestComponent, this, &InstanceManager::SignalCompStateChangeRequest);
			connect(this, &InstanceManager::SignalSetNewComponentStateToAllInstances, lightingInstance.get(), &LightingInstance::setNewComponentState);

			_startingInstances.insert(inst, lightingInstance);

			instanceThread->start();

			_instanceTable->setLastUse(inst);
			_instanceTable->setEnable(inst, true);

			if (!_pendingRequests.contains(inst) && caller != nullptr)
			{
				PendingRequests newDef{ caller, tan };
				_pendingRequests[inst] = newDef;
			}

			return true;
		}
		Debug(_log, "Can't start Overglow instance index '{:d}' with name '{:s}' it's already running or queued for start", inst, (_instanceTable->getNamebyIndex(inst)));
		return false;
	}
	Debug(_log, "Can't start Overglow instance index '{:d}' it doesn't exist in DB", inst);
	return false;
}

bool InstanceManager::stopInstance(quint8 instance)
{
	// inst 0 can't be stopped
	if (!isInstAllowed(instance))
		return false;

	if (_instanceTable->instanceExist(instance))
	{
		if (_runningInstances.contains(instance))
		{
			disconnect(this, nullptr, _runningInstances.value(instance).get(), nullptr);

			_instanceTable->setEnable(instance, false);

			_runningInstances.remove(instance);

			emit SignalInstanceStateChanged(InstanceState::STOP, instance);
			emit SignalInstancesListChanged();

			return true;
		}
		Debug(_log, "Can't stop Overglow instance index '{:d}' with name '{:s}' it's not running'", instance, (_instanceTable->getNamebyIndex(instance)));
		return false;
	}
	Debug(_log, "Can't stop Overglow instance index '{:d}' it doesn't exist in DB", instance);
	return false;
}

bool InstanceManager::createInstance(const QString& name, bool start)
{
	quint8 inst;
	if (_instanceTable->createInstance(name, inst))
	{
		Info(_log, "New Overglow instance created with name '{:s}'", (name));

		if (start)
			startInstance(inst);
		else
			emit SignalInstancesListChanged();

		return true;
	}
	return false;
}

bool InstanceManager::deleteInstance(quint8 inst)
{
	// inst 0 can't be deleted
	if (!isInstAllowed(inst))
		return false;

	// stop it if required as blocking and wait
	bool stopped = stopInstance(inst);

	if (_instanceTable->deleteInstance(inst))
	{
		Info(_log, "Overglow instance with index '{:d}' has been deleted", inst);

		if (!stopped)
			emit SignalInstancesListChanged();

		return true;
	}
	return false;
}

bool InstanceManager::saveName(quint8 inst, const QString& name)
{
	if (_instanceTable->saveName(inst, name))
	{
		emit SignalInstancesListChanged();
		return true;
	}
	return false;
}

void InstanceManager::handleInstanceJustStarted()
{
	LightingInstance* lightingInstance = qobject_cast<LightingInstance*>(sender());
	quint8 instance = lightingInstance->getInstanceIndex();

	if (_startingInstances.contains(instance))
	{
		Info(_log, "Overglow instance '{:s}' has been started", (_instanceTable->getNamebyIndex(instance)));

		std::shared_ptr<LightingInstance> runningInstance = _startingInstances.value(instance);
		_runningInstances.insert(instance, runningInstance);
		_startingInstances.remove(instance);

		emit SignalInstanceStateChanged(InstanceState::START, instance, _instanceTable->getNamebyIndex(instance));
		emit SignalInstancesListChanged();

		if (_pendingRequests.contains(instance))
		{
			PendingRequests def = _pendingRequests.take(instance);
			emit SignalStartInstanceResponse(def.caller, def.tan);
			_pendingRequests.remove(instance);
		}
	}
	else
		Error(_log, "Could not find instance '{:s} (index: {:d})' in the starting list",
			(_instanceTable->getNamebyIndex(instance)), instance);
}

const QJsonObject InstanceManager::getBackup()
{
	return _instanceTable->getBackup();
}


QString InstanceManager::restoreBackup(const QJsonObject& message)
{
	QString error("Empty instance table manager");

	if (_instanceTable != nullptr)
	{
		error = _instanceTable->restoreBackup(message);
	}

	return error;
}

void InstanceManager::saveCalibration(QString saveData)
{
	LightingInstance* instance = InstanceManager::getLightingInstance(0).get();

	if (instance != nullptr)
		QUEUE_CALL_1(instance, saveCalibration, QString, saveData)
	else
		Error(_log, "Overglow instance is not running...can't save the calibration data");
}
