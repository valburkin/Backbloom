
# Define the current source locations
SET(CURRENT_HEADER_DIR ${CMAKE_SOURCE_DIR}/include/led-drivers)
SET(CURRENT_SOURCE_DIR ${CMAKE_SOURCE_DIR}/sources/led-drivers)

IF ( Qt5SerialPort_FOUND OR Qt6SerialPort_FOUND )
	SET( HAVE_SERIAL_LED ON) 
	MESSAGE (STATUS "Found Qt SerialPort library")
	include_directories( dev_serial )
else()
	MESSAGE (WARNING "Not found Qt SerialPort Library. Serial devices are disabled.")
endif()

FILE ( GLOB Leddevice_SOURCES
	"${CURRENT_HEADER_DIR}/*.h"
	"${CURRENT_SOURCE_DIR}/*.cpp"		
	"${CURRENT_HEADER_DIR}/net/*.h"
	"${CURRENT_SOURCE_DIR}/net/*.cpp"
	"${CURRENT_HEADER_DIR}/other/*.h"
	"${CURRENT_SOURCE_DIR}/other/*.cpp"
)

IF ( HAVE_SERIAL_LED )	
	FILE ( GLOB Leddevice_SERIAL_SOURCES "${CURRENT_HEADER_DIR}/serial/*.h" "${CURRENT_SOURCE_DIR}/serial/*.cpp")
endif()

IF ( ENABLE_HID )	
	FILE ( GLOB Leddevice_HID_SOURCES "${CURRENT_HEADER_DIR}/hid/*.h" "${CURRENT_SOURCE_DIR}/hid/*.cpp")
endif()

qt_add_resources(LedDevice_RESOURCES ${CURRENT_SOURCE_DIR}/LedDeviceSchemas.qrc )

SET( Leddevice_SOURCES
	${Leddevice_SOURCES}
	${LedDevice_RESOURCES}
	${Leddevice_SERIAL_SOURCES}
	${Leddevice_HID_SOURCES}
)

add_library(led-drivers OBJECT ${Leddevice_SOURCES} )

target_link_libraries(led-drivers
	Qt${Qt_VERSION}::Core
	Qt${Qt_VERSION}::Network
	linalg
)

IF ( HAVE_SERIAL_LED )
	target_link_libraries(led-drivers Qt${Qt_VERSION}::SerialPort)
endif()

if(WIN32)
	target_link_libraries(led-drivers ws2_32 hid setupapi)
endif()

if (USE_STATIC_QT_PLUGINS)
	target_link_libraries(led-drivers ${STATIC_QT_PLUGINS_LIBS})
endif()

if(USE_PRECOMPILED_HEADERS AND COMMAND target_precompile_headers)
	target_precompile_headers(led-drivers REUSE_FROM precompiled_hyperhdr_headers)
endif()
