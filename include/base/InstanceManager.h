#pragma once

#ifndef PCH_ENABLED
	#include <QMap>
	#include <QStringList>
#endif

#include <image/ColorRgb.h>
#include <utils/VideoBenchmark.h>
#include <utils/Logger.h>
#include <utils/settings.h>
#include <utils/Components.h>
#include <effects/EffectDefinition.h>

class LightingInstance;
class InstanceTable;
class SoundCapture;
class GrabberHelper;

namespace overglow
{
	enum class InstanceState {
		START,
		STOP
	};
};

using namespace overglow;

class InstanceManager : public QObject
{
	Q_OBJECT

public:
	~InstanceManager();

	struct PendingRequests
	{
		QObject* caller;
		int     tan;
	};

	QString getRootPath();
	bool areInstancesReady();

public slots:
	void handleRequestComponent(overglow::Components component, int instanceIndex, bool listen);

	void setSmoothing(int time);


	const QJsonObject getBackup();

	QString restoreBackup(const QJsonObject& message);

	bool IsInstanceRunning(quint8 inst) const { return _runningInstances.contains(inst); }

	std::shared_ptr<LightingInstance> getLightingInstance(quint8 instance = 0);

	QVector<QVariantMap> getInstanceData() const;

	bool startInstance(quint8 inst, QObject* caller = nullptr, int tan = 0, bool disableOnStartup = false);

	bool stopInstance(quint8 inst);

	QJsonObject getAverageColor(quint8 index);

	void toggleStateAllInstances(bool pause = false);

	void toggleGrabbersAllInstances(bool pause, bool includingSystemGrabber, bool includingVideoGrabber);

	void hibernate(bool wakeUp, overglow::SystemComponent source);

	bool createInstance(const QString& name, bool start = false);

	bool deleteInstance(quint8 inst);

	bool saveName(quint8 inst, const QString& name);

	void saveCalibration(QString saveData);

	void handleInstanceStateChange(overglow::InstanceState state, quint8 instance, const QString& name);

	std::vector<QString> getInstances();

	void setInstanceColor(int instance, int priority,ColorRgb ledColors, int timeout_ms);
	void setInstanceEffect(int instance, QString effectName, int priority);
	void clearInstancePriority(int instance, int priority);
	std::list<EffectDefinition> getEffects();

	void handleLutRequestSignal();

signals:
	void SignalInstanceStateChanged(overglow::InstanceState state, quint8 instance, const QString& name = QString());

	void SignalInstancesListChanged();

	void SignalStartInstanceResponse(QObject* caller, const int& tan);

	void SignalSettingsChanged(settings::type type, QJsonDocument data);

	void SignalCompStateChangeRequest(overglow::Components component, bool enable);

	void SignalSetNewComponentStateToAllInstances(overglow::Components component, bool enable);

	void SignalInstancePauseChanged(int instance, bool isEnabled);

	void SignalBenchmarkUpdate(int status, QString message);
	void SignalBenchmarkCapture(int status, QString message);

private slots:
	void handleInstanceJustStarted();

private:
	friend class AppDaemon;

	InstanceManager(const QString& rootPath);

	void startAll(bool disableOnStartup);

	void stopAllonExit();

	bool isInstAllowed(quint8 inst) const { return (inst > 0); }

private:
	
	LoggerName		_log;
	std::unique_ptr<InstanceTable> _instanceTable;
	const QString	_rootPath;
	QMap<quint8, std::shared_ptr<LightingInstance>> _runningInstances;
	QMap<quint8, std::shared_ptr<LightingInstance>>	_startingInstances;

	int		_fireStarter;

	QMap<quint8, PendingRequests> _pendingRequests;
	VideoBenchmark	_videoBenchmark;
};
