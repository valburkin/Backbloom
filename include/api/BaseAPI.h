#pragma once

#ifndef PCH_ENABLED
	#include <QThread>
	#include <memory>
#endif

#include <base/LightingInstance.h>
#include <base/InstanceManager.h>
#include <base/AccessManager.h>
#include <base/GrabberHelper.h>

class SoundCapture;
class DiscoveryWrapper;

class BaseAPI : public QObject
{
	Q_OBJECT

public:
	BaseAPI(const LoggerName& log, bool localConnection, QObject* parent);

	struct ImageCmdData
	{
		int priority = 0;
		QString origin;
		int64_t duration = 0;
		int width = 0;
		int height = 0;
		int scale = 0;
		QString format;
		QString imgName;
		QString imagedata;
	};

	struct EffectCmdData
	{
		int priority;
		int duration;
		QString pythonScript;
		QString origin;
		QString effectName;
		QString data;
		QJsonObject args;
	};


	struct registerData
	{
		overglow::Components component = overglow::Components::COMP_INVALID;
		QString origin;
		QString owner;
		overglow::Components callerComp = overglow::Components::COMP_INVALID;
	};

	typedef std::map<int, registerData> MapRegister;
	typedef QMap<QString, AccessManager::AuthDefinition> MapAuthDefs;

protected:
	virtual void stopDataConnections() = 0;
	virtual void removeSubscriptions() = 0;
	virtual void addSubscriptions() = 0;

	void init();	

	bool clearPriority(int priority, QString& replyMsg, overglow::Components callerComp = overglow::COMP_INVALID);
	std::map<overglow::Components, bool> getAllComponents();
	QVector<QVariantMap> getAllInstanceData();
	QJsonObject getAverageColor(quint8 index);
	quint8 getCurrentInstanceIndex();
	bool isInstanceEnabled();
	void registerInput(int priority, overglow::Components component, const QString& origin, const QString& owner, overglow::Components callerComp);
	void unregisterInput(int priority);

	void setColor(int priority, const std::vector<uint8_t>& ledColors, int timeout_ms = -1, const QString& origin = "API", overglow::Components callerComp = overglow::COMP_INVALID);
	bool setComponentState(const QString& comp, bool& compState, QString& replyMsg, overglow::Components callerComp = overglow::COMP_INVALID);
	bool setEffect(const EffectCmdData& dat, overglow::Components callerComp = overglow::COMP_INVALID);
	bool switchToInstance(quint8 inst);
	bool setImage(ImageCmdData& data, overglow::Components comp, QString& replyMsg, overglow::Components callerComp = overglow::COMP_INVALID);
	void setLedMappingType(int type, overglow::Components callerComp = overglow::COMP_INVALID);
	void setSourceAutoSelect(bool state, overglow::Components callerComp = overglow::COMP_INVALID);
	void setVisiblePriority(int priority, overglow::Components callerComp = overglow::COMP_INVALID);
	
	bool startInstance(quint8 index, int tan = 0);
	void stopInstance(quint8 index);

	//////////////////////////////////
	/// AUTH / ADMINISTRATION METHODS
	//////////////////////////////////

	bool deleteInstance(quint8 index, QString& replyMsg);
	QString createInstance(const QString& name);
	QString setInstanceName(quint8 index, const QString& name);
	bool saveSettings(const QJsonObject& data);
	bool isAuthorized();
	bool isAdminAuthorized();
	bool updatePassword(const QString& password, const QString& newPassword);
	QString createToken(const QString& comment, AccessManager::AuthDefinition& def);
	QString renameToken(const QString& id, const QString& comment);
	QString deleteToken(const QString& id);
	void setNewTokenRequest(const QString& comment, const QString& id, const int& tan);
	void cancelNewTokenRequest(const QString& comment, const QString& id);
	bool handlePendingTokenRequest(const QString& id, bool accept);
	bool getTokenList(QVector<AccessManager::AuthDefinition>& def);
	bool getPendingTokenRequests(QVector<AccessManager::AuthDefinition>& map);
	bool isUserTokenAuthorized(const QString& userToken);
	bool getUserToken(QString& userToken);
	bool isTokenAuthorized(const QString& token);
	bool isUserAuthorized(const QString& password);
	bool isUserBlocked();
	bool hasDefaultPassword();
	void logout();
	void putSystemInfo(QJsonObject& system);
	
	bool _adminAuthorized;

	LoggerName _log;
	std::shared_ptr<InstanceManager>	_instanceManager;
	std::shared_ptr<AccessManager>		_accessManager;	
	std::shared_ptr<LightingInstance>	_instance;
	std::shared_ptr<SoundCapture> _soundCapture;
	std::shared_ptr<GrabberHelper> _systemGrabber;
	std::shared_ptr<PerformanceCounters> _performanceCounters;
	std::shared_ptr<DiscoveryWrapper> _discoveryWrapper;

	struct {
		bool	init = false;
		QString cpuModelType;
		QString cpuModelName;
		QString cpuHardware;
		QString cpuRevision;

		QString kernelType;
		QString kernelVersion;
		QString architecture;
		QString wordSize;
		QString productType;
		QString productVersion;
		QString prettyName;
		QString hostName;
		QString domainName;
	} _sysInfo;

signals:	
	void SignalPendingTokenClientNotification(const QString& id, const QString& comment);
	void SignalPerformClientDisconnection();
	void SignalTokenClientNotification(bool success, const QString& token, const QString& comment, const QString& id, const int& tan);
	void SignalInstanceStartedClientNotification(const int& tan);

private slots:
	void requestActiveRegister(QObject* callerInstance);

private:
	bool _authorized;
	bool _localConnection;
	quint8 _currentInstanceIndex;

	std::map<int, registerData> _activeRegisters;	
};
