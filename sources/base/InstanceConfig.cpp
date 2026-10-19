#ifndef PCH_ENABLED
	#include <QMutexLocker>
#endif

#include <base/InstanceConfig.h>
#include <base/SettingsMigration.h>
#include <json-utils/JsonUtils.h>
#include <db/SettingsTable.h>
#include <json-utils/jsonschema/QJsonUtils.h>
#include <json-utils/jsonschema/QJsonSchemaChecker.h>
#include <led-drivers/LedDeviceManufactory.h>

#include <algorithm>

// Settings types of features that were removed from this product (protobuf/flatbuffers servers, forwarder, raw UDP server,
// boblight, TCP JSON server, the capture card stack: video grabber, its instance control, signal detection calibration
// and automatic tone mapping)
static const QStringList OBSOLETE_SETTINGS = { "protoServer", "flatbufServer", "forwarder", "rawUdpServer", "boblightServer", "jsonServer",
	"videoGrabber", "videoControl", "videoDetection", "automaticToneMapping" };

QJsonObject	InstanceConfig::_schemaJson;
QMutex		InstanceConfig::_lockerSettingsManager;
std::atomic<bool> InstanceConfig::_backupMade(false);

InstanceConfig::InstanceConfig(bool master, quint8 instanceIndex, QObject* parent)
	: QObject(parent)
	, _log(QString("INSTANCE_CFG%1").arg((master) ? QString() : QString::number(instanceIndex)))
{
	QMutexLocker locker(&_lockerSettingsManager);

	Info(_log, "Loading instance configuration");

	_sTable = std::make_unique<SettingsTable>(instanceIndex);

	// get schema
	if (_schemaJson.isEmpty())
	{
		Q_INIT_RESOURCE(resource);
		try
		{
			_schemaJson = QJsonUtils::readSchema(":/settings-schema");
		}
		catch (const std::runtime_error& error)
		{
			throw std::runtime_error(error.what());
		}
	}

	// get default config
	QJsonObject defaultConfig;
	for (settings::type selector = settings::type::SNDEFFECT; selector != settings::type::INVALID; selector = settings::type(((int)selector) + 1))
	{
		if (selector != settings::type::LEDS)
			defaultConfig.insert(typeToString(selector), QJsonObject());
		else
			defaultConfig.insert(typeToString(selector), QJsonArray());
	}

	// transform json to string lists
	QStringList keyList = defaultConfig.keys();
	QStringList defValueList;
	for (const auto& key : keyList)
	{
		if (defaultConfig[key].isObject())
		{
			defValueList << QString(QJsonDocument(defaultConfig[key].toObject()).toJson(QJsonDocument::Compact));
		}
		else if (defaultConfig[key].isArray())
		{
			defValueList << QString(QJsonDocument(defaultConfig[key].toArray()).toJson(QJsonDocument::Compact));
		}
	}

	// fill database with default data if required
	bool hasData = false;
	for (const auto& key : keyList)
	{
		QString val = defValueList.takeFirst();
		// prevent overwrite
		if (!_sTable->recordExist(key))
			_sTable->createSettingsRecord(key, val);
		else
			hasData = true;
	}
	if (!hasData && !_backupMade)
		_backupMade = true;

	// need to validate all data in database constuct the entire data object
	// TODO refactor schemaChecker to accept QJsonArray in validate(); QJsonDocument container? To validate them per entry...
	QJsonObject dbConfig;
	for (const auto& key : keyList)
	{
		QJsonDocument doc = _sTable->getSettingsRecord(key);

		if (key == typeToString(settings::type::LEDS) && doc.isArray())
		{
			QJsonArray entries = doc.array();

			for (int i = 0; i < entries.count(); i++)
			{
				QJsonObject ledItem = entries[i].toObject();
				if (!ledItem.contains("group")) {
					ledItem["group"] = 0;
					entries[i] = ledItem;
				}
			}

			dbConfig[key] = entries;
		}
		else if (doc.isArray())
			dbConfig[key] = doc.array();
		else
			dbConfig[key] = doc.object();
	}

	// upgrade old databases first, so that the schema validation below only reports real problems
	bool upgradeNeeded = upgradeDB(dbConfig);
	upgradeNeeded |= removeUnsupportedFeatures(dbConfig);

	// settings records of removed features (they are not part of dbConfig, so they are deleted from the table directly)
	QStringList obsoleteRecords;
	for (const auto& type : OBSOLETE_SETTINGS)
		if (_sTable->anyRecordExist(type))
			obsoleteRecords.append(type);
	upgradeNeeded |= !obsoleteRecords.isEmpty();

	// validate full dbconfig against schema, on error we need to rewrite entire table
	QJsonSchemaChecker schemaChecker;
	schemaChecker.setSchema(_schemaJson);
	QPair<bool, bool> valid = schemaChecker.validate(dbConfig);

	if (!valid.second)
	{
		for (auto& schemaError : schemaChecker.getMessages())
			Error(_log, "Schema Syntax Error: {:s}", (schemaError));
		throw std::runtime_error("The config schema has invalid syntax. This should never happen! Go fix it!");
	}
	if (!valid.first || upgradeNeeded)
	{
		Info(_log, "Table upgrade required...");
		if (!_backupMade.exchange(true))
		{
			_backupMade = true;
			Info(_log, "Creating DB backup first.");
			QString resultFile = _sTable->createLocalBackup();
			if (!resultFile.isEmpty())
				Info(_log, "The backup is saved as: {:s}", (resultFile));
			else
				Warning(_log, "Could not create a backup");
		}
		Info(_log, "Upgrading...");
		for (const auto& type : std::as_const(obsoleteRecords))
		{
			if (_sTable->purge(type))
				Info(_log, "Removed the '{:s}' settings: the feature is not part of this version", (type));
			else
				Warning(_log, "Could not remove the obsolete '{:s}' settings", (type));
		}

		dbConfig = schemaChecker.getAutoCorrectedConfig(dbConfig);

		for (auto& schemaError : schemaChecker.getMessages())
			Warning(_log, "Config Fix: {:s}", (schemaError));

		saveSettings(dbConfig, true);
	}
	else
		_qconfig = dbConfig;

	Info(_log, "Settings database initialized");
}

bool InstanceConfig::upgradeDB(QJsonObject& dbConfig)
{
	auto generalObject = dbConfig["general"].toObject();
	int version = generalObject["version"].toInt(-1);
	int orgVersion = version;

	if (version < 0)
	{
		Warning(_log, "Could not read DB version");
		return false;
	}

	// settings format v20 update
	if (version < 2)
	{
		auto colorObject = dbConfig["color"].toObject();
		if (colorObject.contains("channelAdjustment"))
		{
			QJsonArray adjUpdate = colorObject["channelAdjustment"].toArray(), newArray;
			for (auto iter = adjUpdate.begin(); iter != adjUpdate.end(); ++iter)
			{
				QJsonObject newObject = (iter)->toObject();
				int threshold = newObject["backlightThreshold"].toInt(0);
				if (threshold > 1)
				{
					newObject["backlightThreshold"] = threshold - 1;
				}
				newArray.append(newObject);
			}
			colorObject["channelAdjustment"] = newArray;
			dbConfig["color"] = colorObject;
			///////////////////////////////////////////////////////////
			version = 2;
			Info(_log, "DB has been upgraded to version: {:d}", version);
		}
	}

	// v3 removed the boblight server: its settings are deleted together with other removed features (OBSOLETE_SETTINGS)
	if (version < 3)
	{
		version = 3;
	}

	// v4 renamed the 'awa_spi' LED driver: SPI drivers are no longer part of this product, see removeUnsupportedFeatures
	if (version < 4)
	{
		version = 4;
	}

	if (version < 5)
	{
		auto colorObject = dbConfig["color"].toObject();
		if (colorObject.contains("channelAdjustment"))
		{
			QJsonArray adjUpdate = colorObject["channelAdjustment"].toArray(), newArray;
			for (auto iter = adjUpdate.begin(); iter != adjUpdate.end(); ++iter)
			{
				QJsonObject newObject = (iter)->toObject();
				double threshold = newObject["backlightThreshold"].toDouble(0);
				if (threshold > 0)
				{
					newObject["backlightThreshold"] = 0.0039;
				}
				else
				{
					newObject["backlightThreshold"] = 0.0;
				}
				newArray.append(newObject);
			}
			colorObject["channelAdjustment"] = newArray;
			dbConfig["color"] = colorObject;
			///////////////////////////////////////////////////////////
			version = 5;			
		}
	}

	if (version < 6)
	{		
		if (!generalObject["disableOnLocked"].toBool())
		{
			Info(_log, "Enabling disableOnLocked for better compatibility with software grabbers");
			generalObject["disableOnLocked"] = true;
		}
		version = 6;
		Info(_log, "DB has been upgraded to version: {:d}", version);
	}

	generalObject["version"] = version;
	dbConfig["general"] = generalObject;

	return orgVersion != version;
}

// Databases created by full Overglow builds may refer to features that this product does not ship.
// The DB version is global, but these settings are partly per instance, so the cleanup is content based
// and runs for every instance (it is idempotent and also covers restored backups).
bool InstanceConfig::removeUnsupportedFeatures(QJsonObject& dbConfig)
{
	bool changed = false;

	for (const QString& change : SettingsMigration::removeUnsupportedFeatures(dbConfig))
	{
		Info(_log, "{:s}", (change));
		changed = true;
	}

	// LED device: fall back to the 'file' driver (no physical output) if the configured driver is not available
	const QString deviceKey = settings::typeToString(settings::type::DEVICE);
	QJsonObject deviceObject = dbConfig[deviceKey].toObject();
	const QString deviceType = deviceObject["type"].toString("file").toLower();
	const auto& drivers = overglow::leds::GET_ALL_LED_DEVICE(nullptr);

	if (std::none_of(drivers.begin(), drivers.end(), [&deviceType](const auto& driver) { return driver.name == deviceType; }))
	{
		Warning(_log, "The LED device type '{:s}' is not supported by this version. The LED output has been switched to the 'file' device (no physical output). Please select a supported LED device in the LED hardware settings.", (deviceType));

		QJsonObject fallback;
		fallback["type"] = "file";
		fallback["output"] = "/dev/null";
		fallback["printTimeStamp"] = false;
		for (const auto& key : { "colorOrder", "refreshTime" })
			if (deviceObject.contains(key))
				fallback[key] = deviceObject[key];

		dbConfig[deviceKey] = fallback;
		changed = true;
	}

	return changed;
}

InstanceConfig::~InstanceConfig()
{
	Debug(_log, "InstanceConfig has been deleted");
}

QJsonDocument InstanceConfig::getSetting(settings::type type) const
{
	return _sTable->getSettingsRecord(settings::typeToString(type));
}

QJsonObject InstanceConfig::getSettings() const
{
	QJsonObject dbConfig;
	for (const auto& key : _qconfig.keys())
	{
		QJsonDocument doc = _sTable->getSettingsRecord(key);
		if (doc.isArray())
			dbConfig[key] = doc.array();
		else
			dbConfig[key] = doc.object();
	}
	return dbConfig;
}

bool InstanceConfig::isReadOnlyMode()
{
	return _sTable->isReadOnlyMode();
}

bool InstanceConfig::saveSettings(QJsonObject config, bool correct)
{
	QJsonSchemaChecker schemaChecker;
	schemaChecker.setSchema(_schemaJson);
	if (!schemaChecker.validate(config).first)
	{
		if (!correct)
		{
			Error(_log, "Failed to save configuration, errors during validation");
			return false;
		}
		Warning(_log, "Fixing json data!");
		config = schemaChecker.getAutoCorrectedConfig(config);

		for (const auto& schemaError : schemaChecker.getMessages())
			Warning(_log, "Config Fix: {:s}", (schemaError));
	}

	// store the new config
	_qconfig = config;

	// extract keys and data
	QStringList keyList = config.keys();
	QStringList newValueList;
	for (const auto& key : keyList)
	{
		if (config[key].isObject())
		{
			newValueList << QString(QJsonDocument(config[key].toObject()).toJson(QJsonDocument::Compact));
		}
		else if (config[key].isArray())
		{
			newValueList << QString(QJsonDocument(config[key].toArray()).toJson(QJsonDocument::Compact));
		}
	}

	int rc = true;
	// compare database data with new data to emit/save changes accordingly
	for (const auto& key : keyList)
	{
		QString data = newValueList.takeFirst();
		QString recordData = _sTable->getSettingsRecordString(key);
		if (recordData != data)
		{
			if (!_sTable->createSettingsRecord(key, data))
			{
				rc = false;
			}
			else
			{
				emit SignalInstanceSettingsChanged(settings::stringToType(key), QJsonDocument::fromJson(data.toUtf8()));
			}
		}
	}
	return rc;
}

void InstanceConfig::saveSetting(settings::type key, QString saveData)
{
	if (!_sTable->createSettingsRecord(settings::typeToString(key), saveData))
	{
		Error(_log, "Could not save the config: {:s} = {:s}", (settings::typeToString(key)), (saveData));
	}
	else
	{
		emit SignalInstanceSettingsChanged(key, QJsonDocument::fromJson(saveData.toUtf8()));
	}
}
