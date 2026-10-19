
# Define the current source locations
SET(CURRENT_HEADER_DIR ${CMAKE_SOURCE_DIR}/include/led-drivers)
SET(CURRENT_SOURCE_DIR ${CMAKE_SOURCE_DIR}/sources/led-drivers)

FILE ( GLOB Leddevice_SOURCES
	"${CURRENT_HEADER_DIR}/*.h"
	"${CURRENT_SOURCE_DIR}/*.cpp"		
	"${CURRENT_HEADER_DIR}/net/*.h"
	"${CURRENT_SOURCE_DIR}/net/*.cpp"
	"${CURRENT_HEADER_DIR}/other/*.h"
	"${CURRENT_SOURCE_DIR}/other/*.cpp"
	"${CURRENT_HEADER_DIR}/serial/*.h"
	"${CURRENT_SOURCE_DIR}/serial/*.cpp"
	"${CURRENT_HEADER_DIR}/hid/*.h"
	"${CURRENT_SOURCE_DIR}/hid/*.cpp"
)

qt_add_resources(LedDevice_RESOURCES ${CURRENT_SOURCE_DIR}/LedDeviceSchemas.qrc )

SET( Leddevice_SOURCES
	${Leddevice_SOURCES}
	${LedDevice_RESOURCES}
)

add_library(led-drivers OBJECT ${Leddevice_SOURCES} )

target_link_libraries(led-drivers
	Qt6::Core
	Qt6::Network
	Qt6::SerialPort
	linalg
	ws2_32
	hid
	setupapi
)

if(USE_PRECOMPILED_HEADERS AND COMMAND target_precompile_headers)
	target_precompile_headers(led-drivers REUSE_FROM precompiled_headers)
endif()
