#pragma once

#include <OverglowConfig.h>

#ifndef PCH_ENABLED	
	#include <QObject>
	#include <QJsonObject>
	#include <QStringList>
#endif

#include <QCoreApplication>

#include <grabber/windows/DX/DxWrapper.h>
#include <mqtt/mqtt.h>
#include <bonjour/DiscoveryWrapper.h>
#include <sound-capture/windows/SoundCaptureWindows.h>
#include <suspend-handler/SuspendHandlerWindows.h>

#include <utils/Logger.h>

// settings management
#include <utils/settings.h>
#include <utils/Components.h>
#include <memory>

class InstanceManager;
class PerformanceCounters;
class SysTray;
class WebServer;
class InstanceConfig;
class AccessManager;
class NetOrigin;
class VideoThread;
class GrabberHelper;
class QApplication;

namespace overglow { enum class InstanceState; }

class AppDaemon : public QObject
{
	Q_OBJECT


public:
	AppDaemon(const QString& rootPath, QCoreApplication* parent, bool logLvlOverwrite);
	~AppDaemon();

	QJsonDocument getSetting(settings::type type) const;	

public slots:
	void freeObjects();
	quint16 getWebPort();

public slots:
	void instanceStateChangedHandler(overglow::InstanceState state, quint8 instance, const QString& name = QString());
	void settingsChangedHandler(settings::type type, const QJsonDocument config);
	void getInstanceManager(std::shared_ptr<InstanceManager>& retVal);
	void getAccessManager(std::shared_ptr<AccessManager>& retVal);
	void getSoundCapture(std::shared_ptr<SoundCapture>& retVal);
	void getSystemGrabber(std::shared_ptr<GrabberHelper>& systemGrabber);
	void getPerformanceCounters(std::shared_ptr<PerformanceCounters>& performanceCounters);
	void getDiscoveryWrapper(std::shared_ptr<DiscoveryWrapper>& discoveryWrapper);

private:
	void startNetworkServices();
	void createSoftwareGrabberHelper(QJsonDocument config, QString deviceName, QString rootPath);

	LoggerName					_log;
	std::shared_ptr<InstanceManager> _instanceManager;
	std::shared_ptr<AccessManager> _accessManager;
	std::shared_ptr<NetOrigin> _netOrigin;
	std::shared_ptr<GrabberHelper> _systemGrabber;
	std::shared_ptr<SoundCapture>  _soundCapture;
	std::shared_ptr<PerformanceCounters> _performanceCounters;
	std::shared_ptr<DiscoveryWrapper> _discoveryWrapper;
	std::unique_ptr<QThread, std::function<void(QThread*)>> _networkThread;
	std::unique_ptr<QThread, std::function<void (QThread*)>> _mqttThread;
	std::unique_ptr<SuspendHandler> _suspendHandler;
	std::unique_ptr<InstanceConfig> _instanceZeroConfig;

	QString				_rootPath;
	bool				_disableOnStart;
};

