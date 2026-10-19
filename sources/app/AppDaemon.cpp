/* AppDaemon.cpp
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
	#include <QTimer>
	#include <QCoreApplication>
	#include <QResource>
	#include <QLocale>
	#include <QFile>
	#include <QString>
	#include <QJsonDocument>
	#include <QJsonObject>
	#include <QJsonValue>
	#include <QPair>
	#include <cstdint>
	#include <limits>
	#include <QThread>

	#include <cassert>
	#include <cstdlib>
#endif

#include <utils/Components.h>
#include <image/Image.h>
#include <utils/GlobalSignals.h>
#include <base/LightingInstance.h>
#include <base/GrabberHelper.h>

#include <webserver/WebServer.h>

// instance zero default configuration
#include <base/InstanceConfig.h>

// AccessManager
#include <base/AccessManager.h>

// InstanceManager Overglow
#include <base/InstanceManager.h>

// NetOrigin checks
#include <utils/NetOrigin.h>

#include <performance-counters/PerformanceCounters.h>

#include "AppDaemon.h"

AppDaemon::AppDaemon(const QString& rootPath, QCoreApplication* parent, bool logLvlOverwrite)
	: QObject(parent)
	, _log("DAEMON")
	, _instanceManager(nullptr)
	, _accessManager(nullptr)
	, _netOrigin(nullptr)
	, _systemGrabber(nullptr)
	, _soundCapture(nullptr)
	, _performanceCounters(nullptr)
	, _discoveryWrapper(nullptr)
	, _networkThread(nullptr)
	, _mqttThread(nullptr)
	, _suspendHandler(nullptr)
	, _rootPath(rootPath)
	, _disableOnStart(false)
{

	// Register metas for thread queued connection
	qRegisterMetaType<ColorRgb>("ColorRgb");
	qRegisterMetaType<SharedOutputColors>("SharedOutputColors");
	qRegisterMetaType<Image<ColorRgb>>("Image<ColorRgb>");
	qRegisterMetaType<overglow::Components>("overglow::Components");
	qRegisterMetaType<settings::type>("settings::type");
	qRegisterMetaType<QMap<quint8, QJsonObject>>("QMap<quint8,QJsonObject>");
	qRegisterMetaType<std::vector<ColorRgb>>("std::vector<ColorRgb>");
	qRegisterMetaType<QVector<ColorRgb>>("QVector<ColorRgb>");

	// First load default configuration for other objects
	_instanceZeroConfig = std::make_unique<InstanceConfig>(true, 0, this);

	// Instance manager
	_instanceManager = std::shared_ptr<InstanceManager>(new InstanceManager(rootPath),
		[](InstanceManager* instanceManager) {
			SMARTPOINTER_MESSAGE("InstanceManager");
			delete instanceManager;
		});
	connect(GlobalSignals::getInstance(), &GlobalSignals::SignalGetInstanceManager, this, &AppDaemon::getInstanceManager, Qt::DirectConnection);

	// Access Manager
	_accessManager = std::shared_ptr<AccessManager>(new AccessManager(this),
		[](AccessManager* accessManager) {
			SMARTPOINTER_MESSAGE("AccessManager");
			delete accessManager;
		});
	connect(GlobalSignals::getInstance(), &GlobalSignals::SignalGetAccessManager, this, &AppDaemon::getAccessManager, Qt::DirectConnection);
	connect(_instanceManager.get(), &InstanceManager::SignalSettingsChanged, _accessManager.get(), &AccessManager::handleSettingsUpdate);
	_accessManager->handleSettingsUpdate(settings::type::NETWORK, getSetting(settings::type::NETWORK));

	// Performance Counters
	_performanceCounters = std::shared_ptr<PerformanceCounters>(new PerformanceCounters(),
		[](PerformanceCounters* performanceCounters) {
			SMARTPOINTER_MESSAGE("PerformanceCounters");
			delete performanceCounters;
		});
	connect(GlobalSignals::getInstance(), &GlobalSignals::SignalGetPerformanceCounters, this, &AppDaemon::getPerformanceCounters, Qt::DirectConnection);

	// Discovery Wrapper
	_discoveryWrapper = std::shared_ptr<DiscoveryWrapper>(new DiscoveryWrapper(this),
		[](DiscoveryWrapper* bonjourBrowserWrapper) {
			SMARTPOINTER_MESSAGE("DiscoveryWrapper");
			delete bonjourBrowserWrapper;
		});
	connect(GlobalSignals::getInstance(), &GlobalSignals::SignalGetDiscoveryWrapper, this, &AppDaemon::getDiscoveryWrapper, Qt::DirectConnection);

	_netOrigin = std::shared_ptr<NetOrigin>(new NetOrigin(),
		[](NetOrigin* netOrigin) {
			SMARTPOINTER_MESSAGE("NetOrigin");
			delete netOrigin;
		});
	connect(_instanceManager.get(), &InstanceManager::SignalSettingsChanged, _netOrigin.get(), &NetOrigin::handleSettingsUpdate);
	_netOrigin->handleSettingsUpdate(settings::type::NETWORK, getSetting(settings::type::NETWORK));

	// set inital log lvl if the loglvl wasn't overwritten by arg
	if (!logLvlOverwrite)
	{
		settingsChangedHandler(settings::type::LOGGER, getSetting(settings::type::LOGGER));
	}

	_soundCapture = std::shared_ptr<SoundCapture>(
		new SoundGrabber(getSetting(settings::type::SNDEFFECT), this),
		[](SoundGrabber* soundGrabber) {
			SMARTPOINTER_MESSAGE("SoundGrabber");
			delete soundGrabber;
		});
	connect(GlobalSignals::getInstance(), &GlobalSignals::SignalGetSoundCapture, this, &AppDaemon::getSoundCapture, Qt::DirectConnection);
	connect(_instanceManager.get(), &InstanceManager::SignalSettingsChanged, _soundCapture.get(), &SoundGrabber::settingsChangedHandler);


	// spawn all Overglow instances (non blocking)
	settingsChangedHandler(settings::type::SYSTEMGRABBER, getSetting(settings::type::SYSTEMGRABBER));
	QJsonObject genConfig = getSetting(settings::type::GENERAL).object();
	_disableOnStart = genConfig["disableLedsStartup"].toBool(false);
	_instanceManager->startAll(_disableOnStart);

	//Cleaning up Overglow before quit
	connect(parent, &QCoreApplication::aboutToQuit, this, &AppDaemon::freeObjects);

	// pipe settings changes and component state changes from InstanceManager to Daemon
	connect(_instanceManager.get(), &InstanceManager::SignalInstanceStateChanged, this, &AppDaemon::instanceStateChangedHandler);
	connect(_instanceManager.get(), &InstanceManager::SignalSettingsChanged, this, &AppDaemon::settingsChangedHandler);

	// ---- network services -----
	startNetworkServices();
}

AppDaemon::~AppDaemon() = default;

void AppDaemon::instanceStateChangedHandler(InstanceState state, quint8 /*instance*/, const QString& /*name*/)
{
	// start web server if needed
	if (state == InstanceState::START)
	{
		if (_instanceManager->areInstancesReady())
		{
			// power management
			if (_suspendHandler == nullptr)
			{
				QJsonObject genConfig = getSetting(settings::type::GENERAL).object();
				bool lockedEnable = genConfig["disableOnLocked"].toBool(true);

				Info(_log, "The OS lock/display-suspend handler for the software grabber is: {:s}", ((lockedEnable) ? "enabled" : "disabled"));

				_suspendHandler = std::make_unique<SuspendHandler>(lockedEnable);
				connect(_suspendHandler.get(), &SuspendHandler::SignalHibernate, _instanceManager.get(), &InstanceManager::hibernate);

				if (QAbstractEventDispatcher::instance() != nullptr)
					QAbstractEventDispatcher::instance()->installNativeEventFilter(_suspendHandler.get());
			}

			if (_systemGrabber != nullptr)
			{
				_systemGrabber->linker.acquire(1);
			}

			if (_mqttThread != nullptr && !_mqttThread->isRunning())
			{
				_mqttThread->start();
			}

			if (_networkThread != nullptr && !_networkThread->isRunning())
			{
				_networkThread->start();
			}

			if (_disableOnStart)
			{
				Warning(_log, "The user has disabled LEDs auto-start in the configuration (interface: 'General' tab)");
				_instanceManager->toggleStateAllInstances(false);
			}
		}
	}
}

void AppDaemon::freeObjects()
{
	Info(_log, "Cleaning up Overglow before quit [preparing]");

	emit GlobalSignals::getInstance()->SignalMqttLastWill(QString(), QStringList());

	disconnect(GlobalSignals::getInstance(), nullptr, nullptr, nullptr);
	disconnect(_instanceManager.get(), nullptr, nullptr, nullptr);
	LightingInstance::signalTerminateTriggered();

	Info(_log, "Releasing SmartPointer: SuspendHandler [ 1/12]");
	if (QAbstractEventDispatcher::instance() != nullptr && _suspendHandler != nullptr)
		QAbstractEventDispatcher::instance()->removeNativeEventFilter(_suspendHandler.get());
	_suspendHandler = nullptr;

	Info(_log, "Releasing SmartPointer: MQTT [ 2/12]");
	_mqttThread = nullptr;

	Info(_log, "Releasing SmartPointer: WebServers [ 3/12]");
	_networkThread = nullptr;

	Info(_log, "Stopping: InstanceManager [ 4/12]");
	_instanceManager->stopAllonExit();

	Info(_log, "Releasing SmartPointer: SoundGrabber [ 5/12]");
	_soundCapture = nullptr;

	Info(_log, "Releasing SmartPointer: BonjourBrowserWrapper [ 6/12]");
	_discoveryWrapper = nullptr;

	Info(_log, "Releasing SmartPointer: SoftwareGrabber [ 7/12]");
	_systemGrabber = nullptr;

	Info(_log, "Releasing SmartPointer: InstanceZeroConfiguration [ 8/12]");
	_instanceZeroConfig = nullptr;

	Info(_log, "Releasing SmartPointer: InstanceManager [ 9/12]");
	_instanceManager = nullptr;

	Info(_log, "Releasing SmartPointer: AccessManager [10/12]");
	_accessManager = nullptr;

	Info(_log, "Releasing SmartPointer: PerformanceCounters [11/12]");
	_performanceCounters = nullptr;

	Info(_log, "Releasing SmartPointer: NetOrigin [12/12]");
	_netOrigin = nullptr;

	Info(_log, "Resources have been freed [finished]");
}

void AppDaemon::startNetworkServices()
{
	/////////////////////////////////////////
	//			NETWORK THREAD			   //
	/////////////////////////////////////////
	QThread* _netThread = new QThread();
	std::vector<QObject*> networkThreadClients;

	_netThread->setObjectName("NetworkThread");

	WebServer* _webserver = new WebServer(_netOrigin, getSetting(settings::type::WEBSERVER), false);
	_webserver->moveToThread(_netThread);
	networkThreadClients.push_back(_webserver);
	connect(_netThread, &QThread::started, _webserver, &WebServer::initServer);
	connect(_netThread, &QThread::finished, _webserver, &WebServer::deleteLater);
	connect(_instanceManager.get(), &InstanceManager::SignalSettingsChanged, _webserver, &WebServer::handleSettingsUpdate);

	WebServer* _sslWebserver = new WebServer(_netOrigin, getSetting(settings::type::WEBSERVER), true);
	_sslWebserver->moveToThread(_netThread);
	networkThreadClients.push_back(_sslWebserver);
	connect(_netThread, &QThread::started, _sslWebserver, &WebServer::initServer);
	connect(_netThread, &QThread::finished, _sslWebserver, &WebServer::deleteLater);
	connect(_instanceManager.get(), &InstanceManager::SignalSettingsChanged, _sslWebserver, &WebServer::handleSettingsUpdate);

	_networkThread = std::unique_ptr<QThread, std::function<void(QThread*)>>(_netThread,
		[networkThreadClients](QThread* networkThread) {
			THREAD_MULTI_REMOVER(QString("NetworkThread and children"), networkThread, networkThreadClients);
		});

	QThread* mqttThread = new QThread();
	mqtt* _mqtt = new mqtt(getSetting(settings::type::MQTT));
	_mqtt->moveToThread(mqttThread);
	connect(mqttThread, &QThread::started, _mqtt, &mqtt::begin);
	connect(_instanceManager.get(), &InstanceManager::SignalSettingsChanged, _mqtt, &mqtt::handleSettingsUpdate);
	connect(mqttThread, &QThread::finished, _mqtt, &mqtt::deleteLater);

	_mqttThread = std::unique_ptr<QThread, std::function<void(QThread*)>>(mqttThread,
		[_mqtt](QThread* mqttThread) {
			THREAD_REMOVER(QString("MQTT"), mqttThread, _mqtt);
		});
}


QJsonDocument AppDaemon::getSetting(settings::type type) const
{
	if (_instanceZeroConfig == nullptr)
	{
		Error(_log, "Default configuration is not initialized");
		return QJsonDocument();
	}
	else
		return _instanceZeroConfig->getSetting(type);
}

quint16 AppDaemon::getWebPort()
{
	return (quint16)getSetting(settings::type::WEBSERVER).object()["port"].toInt(8090);
}

void AppDaemon::createSoftwareGrabberHelper(QJsonDocument config, QString deviceName, QString rootPath)
{
	_systemGrabber = std::shared_ptr<GrabberHelper>(
			new GrabberHelper(),
			[](GrabberHelper* sgrabber) {
				THREAD_REMOVER(QString("SystemGrabber"), sgrabber->thread(), sgrabber);
			}
	);

	QThread* _systemGrabberThread = new QThread();

	_systemGrabber->moveToThread(_systemGrabberThread);
	connect(_systemGrabberThread, &QThread::started, _systemGrabber.get(), &GrabberHelper::SignalCreateGrabber);
	connect(_systemGrabberThread, &QThread::finished, _systemGrabber.get(), &GrabberHelper::deleteLater);
	connect(_systemGrabber.get(), &GrabberHelper::SignalCreateGrabber, _systemGrabber.get(), [this, config, deviceName, rootPath]() {
		_systemGrabber->linker.release(1);
		SystemWrapper* softwareGrabberInstance = nullptr;

		auto candidate = new DxWrapper(deviceName, _rootPath);
		if (!candidate->isActivated(false))
		{
			Warning(_log, "The system doesn't support the DirectX11 system grabber");
			candidate->deleteLater();
		}
		else
			softwareGrabberInstance = candidate;

		if (softwareGrabberInstance != nullptr)
		{
			_systemGrabber->setGrabber(softwareGrabberInstance);

			connect(_systemGrabber->thread(), &QThread::finished, softwareGrabberInstance, &SystemWrapper::deleteLater);
			connect(_instanceManager.get(), &InstanceManager::SignalSettingsChanged, softwareGrabberInstance, &SystemWrapper::handleSettingsUpdate);
			softwareGrabberInstance->SystemWrapper::handleSettingsUpdate(settings::type::SYSTEMGRABBER, config);
		}
		_systemGrabber->linker.release(1);
	});

	connect(GlobalSignals::getInstance(), &GlobalSignals::SignalGetSystemGrabber, this, &AppDaemon::getSystemGrabber, Qt::DirectConnection);
	_systemGrabberThread->start();
	_systemGrabber->linker.acquire(1);
}

void AppDaemon::getInstanceManager(std::shared_ptr<InstanceManager>& retVal)
{
	retVal = _instanceManager;
};

void AppDaemon::getAccessManager(std::shared_ptr<AccessManager>& retVal)
{
	retVal = _accessManager;
};

void AppDaemon::getSoundCapture(std::shared_ptr<SoundCapture>& retVal)
{
	retVal = _soundCapture;
}

void AppDaemon::getSystemGrabber(std::shared_ptr<GrabberHelper>& systemGrabber)
{
	systemGrabber = _systemGrabber;
}

void AppDaemon::getPerformanceCounters(std::shared_ptr<PerformanceCounters>& performanceCounters)
{
	performanceCounters = _performanceCounters;
}

void AppDaemon::getDiscoveryWrapper(std::shared_ptr<DiscoveryWrapper>& discoveryWrapper)
{
	discoveryWrapper = _discoveryWrapper;
}

void AppDaemon::settingsChangedHandler(settings::type settingsType, const QJsonDocument config)
{
	if (settingsType == settings::type::LOGGER)
	{
		const QJsonObject& logConfig = config.object();

		std::string level = logConfig["level"].toString("warn").toStdString(); // silent warn verbose debug
		if (level == "silent")
		{
			Logger::getInstance()->setLogLevel(Logger::OFF);
		}
		else if (level == "warn")
		{
			Logger::getInstance()->setLogLevel(Logger::LogLevel::WARNING);
		}
		else if (level == "verbose")
		{
			Logger::getInstance()->setLogLevel(Logger::INFO);
		}
		else if (level == "debug")
		{
			Logger::getInstance()->setLogLevel(Logger::DEBUG);
		}
	}

	if (settingsType == settings::type::SNDEFFECT)
	{
	}

	if (settingsType == settings::type::SYSTEMGRABBER && _systemGrabber == nullptr)
	{
		const QJsonObject& grabberConfig = config.object();
		const QString deviceName = grabberConfig["device"].toString("auto");
		createSoftwareGrabberHelper(config, deviceName, _rootPath);
	}
}
