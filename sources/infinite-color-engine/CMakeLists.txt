# Define the current source locations
SET(CURRENT_HEADER_DIR ${CMAKE_SOURCE_DIR}/include/infinite-color-engine)
SET(CURRENT_SOURCE_DIR ${CMAKE_SOURCE_DIR}/sources/infinite-color-engine)

FILE ( GLOB INFINITE_ENGINE_SOURCES "${CURRENT_HEADER_DIR}/*.h"  "${CURRENT_SOURCE_DIR}/*.cpp" )

add_library(infinite-engine OBJECT ${INFINITE_ENGINE_SOURCES})

target_link_libraries(infinite-engine
	Qt6::Core
	Qt6::Network
	linalg
)

if(USE_PRECOMPILED_HEADERS AND COMMAND target_precompile_headers)
    target_precompile_headers(infinite-engine REUSE_FROM precompiled_headers)
endif()
