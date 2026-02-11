Using context7 mcp study this library
https://github.com/reactivemarbles/DynamicData

There's something that I want to improve in our app. Whenever we do interactive sessions we

- first load/peek DLQs
- then we aggregate/categorize
- Then user picks the category
- Then we re-fetch the DLQ

There is several issues with this

- User does not see the categorization as messages are picked, it may take significant amount of time to get all DLQs
- DLQs might keep arriving and we might not get to the point "DLQs are fetched"
- Once we fetch all of them (which may not happen in certain high traffic scenarios), only after that we categorize,
- once user picks the categories, we will have to refetch everything, and this is double the time, double the effort

To solve this issue I want use to use the DynamicData SourceCache and maintain an in-memory cache of messages

- The in-memory cache will be built immediately from picked messages using message ID as a cache key
- We will use the extensive capabilities of dynamic data to create teh categorization on the reactive stream
- We will render the reactive stream immediately and refresh the UI every second (not every single time the data stream
  changes)
- we will continue updating the UI
- User can choose to reschedule the messages at any given point in time, even until the complete categories are loaded
- However, once they pick an option, we will explicitly re-schedule the messages, aka the snapshot of the dynamic data
  at the point user chose the category
- Then, we will re-schedule the messages - only the messages inside the snapshot
- We should also consider the scenario that the same dead letter can dead-letter again - WE SHOULD ONLY RESCHEDULE ONCE.
  we should not end up in an endless cycle EVER

I want you to create a detailed plan, to implement this functionality for the resubmit DLQ by refactoring it. Please
ensure, that this kind of in-memory data cache can be used for any other feature - it should be generic, be based on a "
queue" whether it;s DLQ or any other queue, and support categorization.

There is one more detail - during the lifecycle of any queue, including the DLQ
messages might arrive, and messages might go away. For instance, if we         
consider the active queue, once the message is handled it should be removed        
from the queue, or in case of DLQs, when message is successfully handled, it    
should be removed from the DLQ and this can happen both before we completed it,
and also becuase someone else die. Basically we need to guarantee that         
whatever internal or external factors cause a message we cached to be removed   
from the queue, we also remove it from the cache

Create a deatailed plan to refactor the resubmmit DLQ wiht this approach. Ensure to add necessary unit and integration
tests
